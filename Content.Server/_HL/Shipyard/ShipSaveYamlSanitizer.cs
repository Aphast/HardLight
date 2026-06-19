using Content.Shared.Atmos;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._HL.Shipyard;

/// <summary>
/// Centralized ship-save YAML sanitization logic used by shipyard save/export flows.
/// </summary>
public static class ShipSaveYamlSanitizer
{
    /// <summary>
    /// Marker line stamped at the top of newly-sanitized ship-save YAML so the load path
    /// can skip a redundant sanitizer pass for already-clean saves. Bump the version suffix
    /// when the sanitizer rules change in a way that should re-scrub previously-saved ships.
    /// </summary>
    public const string SanitizedMarkerComment = "# hl-sanitized: 4";

    // Implants that should not persist when found inside implanters during ship save.
    private static readonly HashSet<string> BlockedContainedImplantPrototypes = new(StringComparer.Ordinal)
    {
        "DeathRattleImplantColcomm",
        "RadioImplantColcomm",
        "UplinkImplant",
    };

    // Components stripped from all entities during ship-save export.
    // Add new always-remove component types here.
    //
    // The runtime-only block at the end holds components whose [DataField] persists
    // EntityUid references that only make sense at runtime (action grants, in-flight
    // projectiles, currently-playing sound entities, etc.). On load these refs resolve
    // to EntityUid.Invalid (uid 0), which leaks into transform child sets and spams
    // "system.entity_lookup: Encountered deleted entity 0" on every spatial query.
    private static readonly HashSet<string> FilteredTypes = new(StringComparer.Ordinal)
    {
        "Joint",
        "StationMember",
        "NavMap",
        "ShuttleDeed",
        "IFF",
        "LinkedLifecycleGridParent",
        "DeviceNetwork",
        "DeviceNetworkComponent",
        "UserInterface",
        "Docking",
        "ActionGrant",
        "Mind",
        "MindContainer",
        "VendingMachine",
        "Forensics",
        "ContainmentFieldGenerator",
        // Runtime-only entity-ref state — stripping prevents stale-uid spam at load.
        // NOTE: NetworkConfigurator is intentionally NOT in this list. Its Devices
        // dictionary is player-configured persistent state (device address -> entity
        // UID for manually-scanned grid devices); stripping it would silently lose the
        // operator's setup. On-grid Devices entries are already remapped correctly by
        // the engine's EntityDeserializer.UidMap. Cross-grid entries that resolve to
        // EntityUid.Invalid only show up as a UI inconsistency in the configurator,
        // not as the per-tick lookup spam this filter is fixing.
        "Actions",
        "Projectile",
        "ItemToggleActiveSound",
        "Blocking",
        "Turnstile",
        "SubdermalImplant",
    };

    // Fill components that are normally removed from ship saves.
    private static readonly HashSet<string> FillComponentTypes = new(StringComparer.Ordinal)
    {
        "StorageFill",
        "ContainerFill",
        "EntityTableContainerFill",
        "SurplusBundle",
    };

    // Prototype-level exceptions that are allowed to keep fill components.
    // Add prototype IDs here when forced fill removal breaks a specific entity type.
    private static readonly HashSet<string> FillComponentWhitelistPrototypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AirAlarm",
    };

    // Components that should be marked as missing after removal so reconstruction follows prototype defaults.
    private static readonly HashSet<string> ForcedMissingComponents = new(StringComparer.Ordinal)
    {
        "StorageFill",
        "ContainerFill",
        "EntityTableContainerFill",
        "SurplusBundle",
    };

    // Prototype IDs that should never be included in ship exports.
    // Add non-ship entities here to drop them entirely.
    private static readonly HashSet<string> FilteredPrototypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Machines & circuitboards
        "MachineFlatpacker",
        "CommsComputerCircuitboard",
        "ComputerDNAScanner",
        "ComputerExpeditionDiskPrinter",
        "ComputerFundingAllocation",
        "ComputerPsionicsRecords",
        "ComputerRoboticsControl",
        "ComputerShuttleRecords",
        "ComputerTabletopShuttleAntag",
        "DnaScannerConsoleComputerCircuitboard",
        "IDComputerCircuitboard",
        "StationAiUploadComputer",
        // Vending machines
        "DEBUGVendingMachineAmmoBoxes",
        "DEBUGVendingMachineMagazines",
        "DEBUGVendingMachineRangedWeapons",
        "VendingMachineAmmoPOI",
        "VendingMachineAstroVendPOI",
        "VendingMachineBoozePOI",
        "VendingMachineBountyVendPOI",
        "VendingMachineCigsPOI",
        "VendingMachineEngivendPOI",
        "VendingMachineExpeditionaryFlatpackVend",
        "VendingMachineFlatpackVend",
        "VendingMachineFuelVend",
        "VendingMachineGamesPOI",
        "LessLethalVendingMachinePOI",
        "VendingMachineMediDrobePOI",
        "VendingMachineMercVend",
        "VendingMachinePickNPackPOI",
        "VendingMachinePottedPlantVendPOI",
        "VendingMachineSalvagePOI",
        "VendingMachineSyndieContraband",
        "VendingMachineTankDispenserEVAPOI",
        "VendingMachineVendomatPOI",
        "VendingMachineYouToolPOI",
        // Everything else
        "ContainmentField",
        "PortalBlue",
        "PortalRed",
        "ReactorGasPipe",
        "TurbineGasPipe",
        "ShipShield",
        // HardLight #1267: flesh anomaly cores can serialize runtime refs that break ship load
        // with unresolved MetaDataComponent errors. Exclude them from ship exports.
        "AnomalyCoreFlesh",
        "AnomalyCoreFleshInert",
        // NullSpace items
        "ClothingEyesGlassesNullSpace",
        "BluespaceFlasher",
        "ClothingNullHarness",
        "ClothingNullSpaceTeleporter",
        "GrenadeDePhase",
        "BluespaceFlasherFlatpack"
    };

    // Entity-level exclusion by component signature.
    // If an entity has any of these components it is removed from export,
    // unless allowed by ComponentExclusionExceptions below.
    private static readonly HashSet<string> FilteredEntityByComponentTypes = new(StringComparer.Ordinal)
    {
        "CommunicationsConsole",
        "ContrabandPalletConsole",
        "CriminalRecordsConsole",
        "DnaSequenceInjector",
        "DoorRemote",
        "EmergencyShuttleConsole",
        "GeneralStationRecordConsole",
        "GeneticAnalyzer",
        "Ghost",
        "GhostRole",
        "HumanoidAppearance",
        "IdCard",
        "IdCardConsole",
        "MarketConsole",
        "NFCargoOrderConsole",
        "Pda",
        "ShipyardConsole",
        "Store",
    };

    private static readonly HashSet<string> ActionEntityComponentTypes = new(StringComparer.Ordinal)
    {
        "InstantAction",
        "EntityTargetAction",
        "WorldTargetAction",
        "EntityWorldTargetAction",
    };

    // Component exclusion exceptions: keep entity when both component and its paired exception component exist.
    private static readonly Dictionary<string, string> ComponentExclusionExceptions = new(StringComparer.Ordinal)
    {
        ["ShipyardConsole"] = "ShipyardListing",
    };

    public static void SanitizeShipSaveNode(MappingDataNode root, IPrototypeManager prototypeManager)
    {
        // Keep serialized nullspace empty so ship exports stay scoped to the grid payload.
        try
        {
            root["nullspace"] = new SequenceDataNode();
        }
        catch
        {
            // Best effort: if nullspace cannot be overwritten, continue with entity-level sanitation.
        }

        if (!root.TryGet("entities", out SequenceDataNode? protoSeq) || protoSeq == null)
            return;

        // Track entity UIDs removed during sanitation so we can prune stale container/storage references.
        var removedEntityUids = new HashSet<string>(StringComparer.Ordinal);
        var blockedContainedImplantEntityUids = CollectBlockedContainedImplantEntityUids(protoSeq);

        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            for (var i = 0; i < entitiesSeq.Count; i++)
            {
                if (entitiesSeq[i] is not MappingDataNode entMap)
                    continue;

                // Remove runtime-only map flags from exported entities.
                entMap.Remove("mapInit");
                entMap.Remove("paused");

                var hasProtoGroup = false;
                var allowFillComponents = false;
                HashSet<string>? protoMissing = null;
                var dropByPrototypeComponent = false;
                EntityPrototype? entityProto = null;

                if (protoMap.TryGet("proto", out ValueDataNode? protoIdNode) && protoIdNode != null)
                {
                    hasProtoGroup = true;
                    var protoId = protoIdNode.Value;
                    allowFillComponents = FillComponentWhitelistPrototypes.Contains(protoId);

                    if (FilteredPrototypes.Contains(protoId))
                    {
                        if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                            removedEntityUids.Add(removedUidNode.Value);

                        entitiesSeq.RemoveAt(i);
                        i--;
                        continue;
                    }

                    if (prototypeManager.TryIndex<EntityPrototype>(protoId, out var proto))
                    {
                        entityProto = proto;

                        foreach (var componentName in FilteredEntityByComponentTypes)
                        {
                            if (!proto.Components.ContainsKey(componentName))
                                continue;

                            if (ComponentExclusionExceptions.TryGetValue(componentName, out var exceptionComponent)
                                && proto.Components.ContainsKey(exceptionComponent))
                                continue;

                            dropByPrototypeComponent = true;
                            break;
                        }

                        if (!allowFillComponents)
                        {
                            foreach (var name in ForcedMissingComponents)
                            {
                                if (!proto.Components.ContainsKey(name))
                                    continue;

                                protoMissing ??= new HashSet<string>(StringComparer.Ordinal);
                                protoMissing.Add(name);
                            }
                        }
                    }
                }

                if (!entMap.TryGet("components", out SequenceDataNode? comps) || comps == null)
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                    continue;
                }

                if (HasActionEntityComponentNode(comps))
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                    continue;
                }

                // Remove implanters containing blocked implant entities.
                var isImplanter = entityProto?.Components.ContainsKey("Implanter") == true || HasComponentNode(comps, "Implanter");
                if (isImplanter && HasBlockedContainedImplant(entMap, blockedContainedImplantEntityUids))
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                    continue;
                }

                var dropByComponent = false;
                foreach (var c in comps)
                {
                    if (c is not MappingDataNode cm)
                        continue;

                    if (!cm.TryGet("type", out ValueDataNode? t) || t == null)
                        continue;

                    if (!FilteredEntityByComponentTypes.Contains(t.Value))
                        continue;

                    if (ComponentExclusionExceptions.TryGetValue(t.Value, out var exceptionComponent)
                        && (HasComponentNode(comps, exceptionComponent)
                            || entityProto?.Components.ContainsKey(exceptionComponent) == true))
                        continue;

                    dropByComponent = true;
                    break;
                }

                if (dropByComponent)
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                    continue;
                }

                if (dropByPrototypeComponent)
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                    continue;
                }

                // Grid root gets slightly different Transform sanitation.
                var hasMapGrid = false;
                var compsNotNull = comps;
                var paintStylePrototype = GetPaintStylePrototype(compsNotNull);

                foreach (var c in compsNotNull)
                {
                    if (c is MappingDataNode cm && cm.TryGet("type", out ValueDataNode? t) && t != null && t.Value == "MapGrid")
                    {
                        hasMapGrid = true;
                        break;
                    }
                }

                var hasDockingComponent = false;
                foreach (var c in compsNotNull)
                {
                    if (c is not MappingDataNode cm || !cm.TryGet("type", out ValueDataNode? t) || t == null)
                        continue;

                    if (t.Value == "Docking")
                        hasDockingComponent = true;
                }

                // Build sanitized component list for this entity.
                var newComps = new SequenceDataNode();
                var removedFromPrototype = hasProtoGroup ? new HashSet<string>(StringComparer.Ordinal) : null;

                foreach (var compNode in compsNotNull)
                {
                    if (compNode is not MappingDataNode compMap)
                        continue;

                    if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                    {
                        newComps.Add(compMap);
                        continue;
                    }

                    var typeName = typeNode.Value;

                    RemoveRuntimeActionReferenceFields(compMap);

                    if (FilteredTypes.Contains(typeName))
                    {
                        if (allowFillComponents && FillComponentTypes.Contains(typeName))
                        {
                            newComps.Add(compMap);
                            continue;
                        }

                        if (ForcedMissingComponents.Contains(typeName))
                            removedFromPrototype?.Add(typeName);

                        continue;
                    }

                    if (typeName == "Transform" && hasMapGrid)
                        compMap.Remove("rot");

                    if (typeName == "AccessReader")
                    {
                        // Keep the configured access behavior, but do not persist historical access logs.
                        compMap.Remove("accessLog");
                        compMap.Remove("AccessLog");
                        compMap.Remove("loggingDisabled");
                        compMap.Remove("LoggingDisabled");
                    }

                    if (hasDockingComponent)
                    {
                        if (typeName == "Door")
                            ResetDockDoorState(compMap);

                        if (typeName == "DoorBolt")
                            ResetDockDoorBoltState(compMap);
                    }

                    if (typeName == "Appearance" && paintStylePrototype != null)
                        ApplyPaintStyleToAppearance(compMap, paintStylePrototype);

                    if (typeName == "SpreaderGrid")
                    {
                        compMap.Remove("updateAccumulator");
                        compMap.Remove("UpdateAccumulator");
                    }

                    if (typeName == "Door")
                    {
                        // VRS: Ships saved while docked can serialize airlock state as Open/Opening
                        // while Docking is stripped from the same entity. On load this leaves doors
                        // visually/physically stuck in an invalid open state (HL #1645).
                        compMap["state"] = new ValueDataNode("Closed");
                        compMap["State"] = new ValueDataNode("Closed");
                    }

                    if (typeName == "DoorBolt")
                    {
                        // Clear runtime bolt latch captured during docking interactions.
                        compMap["boltsDown"] = new ValueDataNode("false");
                        compMap["BoltsDown"] = new ValueDataNode("false");
                    }

                    if (typeName == "VendingMachine")
                    {
                        compMap.Remove("Inventory");
                        compMap.Remove("EmaggedInventory");
                        compMap.Remove("ContrabandInventory");
                        compMap.Remove("Contraband");
                        compMap.Remove("EjectEnd");
                        compMap.Remove("DenyEnd");
                        compMap.Remove("DispenseOnHitEnd");
                        compMap.Remove("NextEmpEject");
                        compMap.Remove("EjectRandomCounter");
                    }

                    if (typeName == "ResearchServer")
                    {
                        compMap.Remove("points");
                        compMap.Remove("Points");
                        compMap.Remove("pointsPerSecond");
                        compMap.Remove("PointsPerSecond");
                    }

                    if (typeName == "TechnologyDatabase")
                    {
                        compMap.Remove("unlockedTechnologies");
                        compMap.Remove("UnlockedTechnologies");
                        compMap.Remove("unlockedRecipes");
                        compMap.Remove("UnlockedRecipes");
                        compMap.Remove("currentTechnologyCards");
                        compMap.Remove("CurrentTechnologyCards");
                        compMap.Remove("mainDiscipline");
                        compMap.Remove("MainDiscipline");
                    }

                    if (typeName == "Shuttle")
                    {
                        // Strip WEP runtime state — these fields are never [DataField] but guard against future changes.
                        compMap.Remove("wepBoostActive");
                        compMap.Remove("wepBoostExpiry");
                        compMap.Remove("wepBoostMaxVelocity");
                        compMap.Remove("wepBleedExpiry");
                        compMap.Remove("wepCooldownExpiry");
                        compMap.Remove("wepThrustMultiplier");
                        compMap.Remove("wepAudioStream");
                        compMap.Remove("wepPowerApplied");
                        compMap.Remove("wepCurrentLoad");
                        compMap.Remove("wepLastLoadUpdateTime");
                    }

                    if (typeName == "Battery")
                    {
                        compMap["currentCharge"] = new ValueDataNode("0");
                        compMap["CurrentCharge"] = new ValueDataNode("0");
                    }

                    if (typeName == "DeviceNetwork")
                    {
                        compMap.Remove("devices");
                        compMap.Remove("Devices");
                    }

                    if (typeName == "AtmosMonitor")
                    {
                        // gasThresholds is a Dictionary<Gas, ...>. If a saved gas key is no longer a
                        // valid Gas enum member (e.g. a gas added then reverted, like Respiron /
                        // HL #2077), the dictionary deserializer throws ("Requested value 'X' was not
                        // found") and the engine drops the WHOLE entity on load — which is why every
                        // saved vent/scrubber vanished. Prune only the unknown-gas entries so any
                        // still-valid custom thresholds survive.
                        PruneUnknownGasThresholds(compMap, "gasThresholds");
                        PruneUnknownGasThresholds(compMap, "GasThresholds");
                    }

                    if (typeName == "SpamEmitSound" && !HasRequiredEmitSound(compMap))
                    {
                        // Runtime ambience state (computer sound effects, HL #2084). When the serialized
                        // diff omits the component's required `sound` field, the engine hits
                        // RequiredFieldNotMappedException and drops the whole entity on load — which is
                        // why every saved console/computer vanished. Drop the incomplete component and
                        // let the prototype/runtime re-supply a valid one. A fully-serialized
                        // SpamEmitSound (with `sound`) is left untouched.
                        continue;
                    }

                    if (typeName == "Solution")
                    {
                        if (compMap.TryGetValue("solution", out var solutionNode)
                            && solutionNode is MappingDataNode solutionMap
                            && solutionMap.TryGetValue("name", out var nameNode)
                            && nameNode is ValueDataNode nameValue
                            && nameValue.Value == "buffer")
                        {
                            solutionMap["canReact"] = new ValueDataNode("false");
                        }
                    }

                    if (typeName == "ReagentDispenser")
                    {
                        compMap.Remove("storageSlots");
                        compMap.Remove("storageSlotIds");
                        compMap.Remove("autoLabel");
                    }

                    newComps.Add(compMap);
                }

                // If SprayPainted exists without Appearance, synthesize Appearance so paint style persists in saves.
                if (paintStylePrototype != null && !HasComponentNode(newComps, "Appearance"))
                {
                    var appearanceComp = new MappingDataNode
                    {
                        ["type"] = new ValueDataNode("Appearance")
                    };

                    ApplyPaintStyleToAppearance(appearanceComp, paintStylePrototype);
                    newComps.Add(appearanceComp);
                }

                if ((removedFromPrototype != null && removedFromPrototype.Count > 0) || (protoMissing != null && protoMissing.Count > 0))
                {
                    var existingMissing = new HashSet<string>(StringComparer.Ordinal);
                    if (entMap.TryGet("missingComponents", out SequenceDataNode? missingNode) && missingNode != null)
                    {
                        foreach (var missing in missingNode)
                        {
                            if (missing is ValueDataNode value)
                                existingMissing.Add(value.Value);
                        }
                    }

                    var mergedSet = new HashSet<string>(existingMissing, StringComparer.Ordinal);
                    if (removedFromPrototype != null)
                    {
                        foreach (var name in removedFromPrototype)
                            mergedSet.Add(name);
                    }

                    if (protoMissing != null)
                    {
                        foreach (var name in protoMissing)
                            mergedSet.Add(name);
                    }

                    if (allowFillComponents)
                    {
                        foreach (var name in FillComponentTypes)
                            mergedSet.Remove(name);
                    }

                    if (mergedSet.Count > 0)
                    {
                        var mergedMissing = new SequenceDataNode();
                        foreach (var name in mergedSet)
                            mergedMissing.Add(new ValueDataNode(name));

                        entMap["missingComponents"] = mergedMissing;
                    }
                }

                if (newComps.Count > 0)
                {
                    entMap["components"] = newComps;
                }
                else
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                }
            }
        }

        // Final pass: remove stale container/storage references to entities dropped above
        // OR to entities that were never in this grid to begin with (e.g. mobs that despawned
        // mid-flight, players who logged off while in storage, parented sub-grids that got
        // cleaned up). Without this, those refs deserialize to EntityUid.Invalid (uid 0) and
        // the engine's RecursiveAdd in EntityLookupSystem spams "Encountered deleted entity 0"
        // on every spatial query for the lifetime of the loaded grid.
        var declaredEntityUids = CollectDeclaredEntityUids(protoSeq);
        PruneContainerReferencesToRemovedEntities(protoSeq, removedEntityUids, declaredEntityUids);
    }

    /// <summary>
    /// Returns the set of every uid string declared as an entity in the save's
    /// <c>entities</c> sequence. Used by <see cref="PruneContainerReferencesToRemovedEntities"/>
    /// to detect dangling references to entities that were never present in this save.
    /// </summary>
    private static HashSet<string> CollectDeclaredEntityUids(SequenceDataNode protoSeq)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is not MappingDataNode entMap)
                    continue;

                if (entMap.TryGet("uid", out ValueDataNode? uidNode) && uidNode != null && !uidNode.IsNull)
                    declared.Add(uidNode.Value);
            }
        }

        return declared;
    }

    private static string? GetPaintStylePrototype(SequenceDataNode components)
    {
        foreach (var compNode in components)
        {
            if (compNode is not MappingDataNode compMap)
                continue;

            if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null || typeNode.Value != "SprayPainted")
                continue;

            if (!compMap.TryGet("paintedPrototype", out ValueDataNode? styleNode) || styleNode == null)
                continue;

            if (!string.IsNullOrWhiteSpace(styleNode.Value))
                return styleNode.Value;
        }

        return null;
    }

    /// <summary>
    /// True if a serialized SpamEmitSound (or other BaseEmitSound) component still carries its
    /// required <c>sound</c> field. Components without it cannot be deserialized and would take the
    /// whole entity down with them, so callers drop the incomplete component instead.
    /// </summary>
    private static bool HasRequiredEmitSound(MappingDataNode compMap)
    {
        return compMap.TryGetValue("sound", out _) || compMap.TryGetValue("Sound", out _);
    }

    /// <summary>
    /// Removes AtmosMonitor gas-threshold entries whose key is not a valid
    /// <see cref="Gas"/> enum member. A single unknown key (e.g. a gas added then reverted) otherwise
    /// throws inside the dictionary deserializer and drops the entire entity on load. Best-effort:
    /// any structural surprise leaves the node untouched rather than throwing.
    /// </summary>
    private static void PruneUnknownGasThresholds(MappingDataNode atmosMonitorComp, string field)
    {
        if (!atmosMonitorComp.TryGet(field, out MappingDataNode? thresholds) || thresholds == null)
            return;

        List<string>? invalid = null;
        foreach (var (key, _) in thresholds)
        {
            if (!Enum.TryParse<Gas>(key, ignoreCase: false, out _))
                (invalid ??= new List<string>()).Add(key);
        }

        if (invalid == null)
            return;

        foreach (var key in invalid)
            thresholds.Remove(key);
    }

    private static bool HasComponentNode(SequenceDataNode components, string componentType)
    {
        foreach (var compNode in components)
        {
            if (compNode is not MappingDataNode compMap)
                continue;

            if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                continue;

            if (typeNode.Value == componentType)
                return true;
        }

        return false;
    }

    private static bool HasActionEntityComponentNode(SequenceDataNode components)
    {
        foreach (var compNode in components)
        {
            if (compNode is not MappingDataNode compMap)
                continue;

            if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                continue;

            if (ActionEntityComponentTypes.Contains(typeNode.Value))
                return true;
        }

        return false;
    }

    private static void RemoveRuntimeActionReferenceFields(MappingDataNode compMap)
    {
        var removeKeys = new List<string>();

        foreach (var (key, _) in compMap)
        {
            if (key.EndsWith("ActionEntity", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("ActionEntities", StringComparison.OrdinalIgnoreCase))
            {
                removeKeys.Add(key);
            }
        }

        foreach (var key in removeKeys)
        {
            compMap.Remove(key);
        }
    }

    private static HashSet<string> CollectBlockedContainedImplantEntityUids(SequenceDataNode protoSeq)
    {
        // Resolve concrete entity UIDs for blocked implant prototypes so implanter checks are cheap per entity.
        var blockedImplantUids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            var protoIsBlockedImplant = false;
            if (protoMap.TryGet("proto", out ValueDataNode? protoIdNode)
                && protoIdNode != null
                && !protoIdNode.IsNull)
            {
                protoIsBlockedImplant = BlockedContainedImplantPrototypes.Contains(protoIdNode.Value);
            }

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is not MappingDataNode entMap)
                    continue;

                if (!entMap.TryGet("uid", out ValueDataNode? uidNode) || uidNode == null || uidNode.IsNull)
                    continue;

                if (protoIsBlockedImplant)
                    blockedImplantUids.Add(uidNode.Value);
            }
        }

        return blockedImplantUids;
    }

    private static bool HasBlockedContainedImplant(MappingDataNode entMap, HashSet<string> blockedContainedImplantEntityUids)
    {
        if (blockedContainedImplantEntityUids.Count == 0)
            return false;

        if (!entMap.TryGet("components", out SequenceDataNode? components) || components == null)
            return false;

        foreach (var compNode in components)
        {
            if (compNode is not MappingDataNode compMap)
                continue;

            if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null || typeNode.Value != "ContainerContainer")
                continue;

            if (!compMap.TryGet("containers", out MappingDataNode? containersMap) || containersMap == null)
                continue;

            if (!containersMap.TryGet("implanter_slot", out MappingDataNode? slotMap) || slotMap == null)
                continue;

            if (slotMap.TryGet("ent", out ValueDataNode? entNode) && entNode != null && !entNode.IsNull && blockedContainedImplantEntityUids.Contains(entNode.Value))
                return true;

            if (!slotMap.TryGet("ents", out SequenceDataNode? entsNode) || entsNode == null)
                continue;

            foreach (var entry in entsNode)
            {
                if (entry is not ValueDataNode valueNode || valueNode.IsNull)
                    continue;

                if (blockedContainedImplantEntityUids.Contains(valueNode.Value))
                    return true;
            }
        }

        return false;
    }

    private static void PruneContainerReferencesToRemovedEntities(
        SequenceDataNode protoSeq,
        HashSet<string> removedEntityUids,
        HashSet<string> declaredEntityUids)
    {
        // A reference is stale if it points at an entity we explicitly removed during
        // sanitation, OR at an entity that was never declared in this save at all.
        // The latter happens for runtime refs that were captured at save time but
        // pointed outside the grid (despawned mobs, off-grid players, parent stations).
        bool IsStale(string uid) => removedEntityUids.Contains(uid) || !declaredEntityUids.Contains(uid);

        // Remove dangling references from both ContainerContainer and Storage serialized structures.
        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is not MappingDataNode entMap)
                    continue;

                if (!entMap.TryGet("components", out SequenceDataNode? comps) || comps == null)
                    continue;

                foreach (var compNode in comps)
                {
                    if (compNode is not MappingDataNode compMap)
                        continue;

                    if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                        continue;

                    var componentType = typeNode.Value;

                    if (componentType == "ContainerContainer")
                    {
                        if (!compMap.TryGet("containers", out MappingDataNode? containersMap) || containersMap == null)
                            continue;

                        foreach (var (_, containerNode) in containersMap)
                        {
                            if (containerNode is not MappingDataNode containerMap)
                                continue;

                            if (containerMap.TryGet("ents", out SequenceDataNode? entsNode) && entsNode != null)
                            {
                                for (var idx = entsNode.Count - 1; idx >= 0; idx--)
                                {
                                    if (entsNode[idx] is not ValueDataNode entValue || entValue.IsNull)
                                        continue;

                                    if (IsStale(entValue.Value))
                                        entsNode.RemoveAt(idx);
                                }
                            }

                            if (containerMap.TryGet("ent", out ValueDataNode? entNode) && entNode != null && !entNode.IsNull)
                            {
                                if (IsStale(entNode.Value))
                                    containerMap["ent"] = ValueDataNode.Null();
                            }
                        }

                        continue;
                    }

                    if (componentType == "Storage" && compMap.TryGet("storedItems", out MappingDataNode? storedItemsMap) && storedItemsMap != null)
                    {
                        var removeKeys = new List<string>();
                        foreach (var (itemUid, _) in storedItemsMap)
                        {
                            if (IsStale(itemUid))
                                removeKeys.Add(itemUid);
                        }

                        foreach (var key in removeKeys)
                            storedItemsMap.Remove(key);
                    }
                }
            }
        }
    }

    private static void ApplyPaintStyleToAppearance(MappingDataNode appearanceComp, string stylePrototype)
    {
        MappingDataNode appearanceDataInit;
        if (appearanceComp.TryGet("appearanceDataInit", out MappingDataNode? existing) && existing != null)
        {
            appearanceDataInit = existing;
        }
        else
        {
            appearanceDataInit = new MappingDataNode();
            appearanceComp["appearanceDataInit"] = appearanceDataInit;
        }

        appearanceDataInit["enum.PaintableVisuals.Prototype"] = new ValueDataNode(stylePrototype);
    }

    /// <summary>
    /// Ship saves should never preserve the "held open because currently docked" runtime state.
    /// Resetting these fields makes docking airlocks load from their normal prototype defaults.
    /// </summary>
    private static void ResetDockDoorState(MappingDataNode doorComp)
    {
        doorComp.Remove("state");
        doorComp.Remove("partial");
        doorComp.Remove("secondsUntilStateChange");
        doorComp.Remove("changeAirtight");
    }

    private static void ResetDockDoorBoltState(MappingDataNode boltComp)
    {
        boltComp.Remove("boltsDown");
    }
}
