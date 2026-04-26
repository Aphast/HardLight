using System.Numerics;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Station.Components;
using Content.Shared.Chat;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Content.Shared.Localizations;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Map; // Frontier
using Content.Server.GameTicking; // Frontier
using Content.Server._NF.Salvage.Expeditions.Structure; // Frontier
using Content.Server._NF.Salvage.Expeditions;
using Content.Shared.Salvage; // Frontier
using RobustTimer = Robust.Shared.Timing.Timer; // HardLight

namespace Content.Server.Salvage;

public sealed partial class SalvageSystem
{
    /*
     * Handles actively running a salvage expedition.
     */

    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!; // Frontier

    private const int ExpeditionReturnPositionRetries = 20; // HardLight
    private const float ExpeditionReturnMinDistance = 200f; // HardLight
    private const float ExpeditionReturnMinRange = 750f; // HardLight
    private const float ExpeditionReturnMaxRange = 3500f; // HardLight

    private void InitializeRunner()
    {
        SubscribeLocalEvent<FTLRequestEvent>(OnFTLRequest);
        SubscribeLocalEvent<FTLStartedEvent>(OnFTLStarted);
        SubscribeLocalEvent<FTLCompletedEvent>(OnFTLCompleted);
        SubscribeLocalEvent<ConsoleFTLAttemptEvent>(OnConsoleFTLAttempt);
    }

    private void OnConsoleFTLAttempt(ref ConsoleFTLAttemptEvent ev)
    {
        if (!TryComp(ev.Uid, out TransformComponent? xform) ||
            !TryGetExpeditionForEntity(ev.Uid, out var expeditionUid, out _, xform))
        {
            return;
        }

        // TODO: This is terrible but need bluespace harnesses or something.
        var query = EntityQueryEnumerator<HumanoidAppearanceComponent, MobStateComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out _, out var mobState, out var mobXform))
        {
            if (!IsEntityOnExpedition(uid, expeditionUid, mobXform))
                continue;

            // Don't count unidentified humans (loot) or anyone you murdered so you can still maroon them once dead.
            if (_mobState.IsDead(uid, mobState))
                continue;

            // Okay they're on salvage, so are they on the shuttle.
            if (mobXform.GridUid != ev.Uid)
            {
                ev.Cancelled = true;
                ev.Reason = Loc.GetString("salvage-expedition-not-all-present");
                return;
            }
        }
    }

    /// <summary>
    /// Announces status updates to salvage crewmembers on the state of the expedition.
    /// </summary>
    private void Announce(EntityUid mapUid, string text)
    {
        // HardLight: announcements race with map teardown at expedition end (FTL-out clears
        // the map while pending Announce calls are still in flight). Both "map component
        // gone" and "MapId no longer registered" are normal during cleanup, so log at Debug.
        if (!TryComp<MapComponent>(mapUid, out var map))
        {
            var xform = Transform(mapUid);
            if (xform.MapUid is not { } parentMap || !TryComp<MapComponent>(parentMap, out map))
            {
                Log.Debug($"Skipping salvage announcement for {ToPrettyString(mapUid)} because the map component is no longer available.");
                return;
            }

            mapUid = parentMap;
        }

        var mapId = map.MapId;

        if (!_mapSystem.TryGetMap(mapId, out var sender) || sender == null || sender == EntityUid.Invalid)
        {
            Log.Debug($"Skipping salvage announcement for {ToPrettyString(mapUid)} because map {mapId} is no longer registered.");
            return;
        }

        // I love TComms and chat!!!
        _chat.ChatMessageToManyFiltered(
            Filter.BroadcastMap(mapId),
            ChatChannel.Radio,
            text,
            text,
            sender.Value, // HardLight: _mapManager.GetMapEntityId(mapId)<sender
            false,
            true,
            null);
    }

    private void OnFTLRequest(ref FTLRequestEvent ev)
    {
        if (!HasComp<SalvageExpeditionComponent>(ev.MapUid) ||
            !TryComp<FTLDestinationComponent>(ev.MapUid, out var dest))
        {
            return;
        }

        // HardLight: Allow multiple shuttles to FTL into an expedition.
        // Previously the destination was disabled after first arrival, blocking reinforcements and recovery ops.
        // Keep destination enabled so long as expedition exists; consoles will continue to show it as a target.
        if (!dest.Enabled)
        {
            dest.Enabled = true;
            Dirty(ev.MapUid, dest);
        }
    }

    private void OnFTLCompleted(ref FTLCompletedEvent args)
    {
        if (!TryGetExpeditionForEntity(args.Entity, out var expeditionUid, out var component))
            return;

        EnsureComp<ExpeditionParticipantShuttleComponent>(args.Entity);

        // Someone FTLd there so start announcement
        if (component.Stage != ExpeditionStage.Added)
            return;

        // HardLight: Update the station's expedition data via the console
        if (component.Console != null && TryComp<SalvageExpeditionConsoleComponent>(component.Console.Value, out var consoleComp))
        {
            var data = GetStationExpeditionData(component.Console.Value);
            if (data != null)
            {
                data.CanFinish = true;
                UpdateConsole((component.Console.Value, consoleComp));
                Log.Info($"FTL completed - enabled finishing for console {ToPrettyString(component.Console.Value)}");
            }
        }
        else
        {
            Log.Warning($"FTL completed but no valid console reference found for expedition on {expeditionUid}");
        }

        if (component.EndTime is { } endTime)
            Announce(expeditionUid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", (endTime - _timing.CurTime).Minutes)));

        var directionLocalization = ContentLocalizationManager.FormatDirection(component.DungeonLocation.GetDir()).ToLower();

        if (component.DungeonLocation != Vector2.Zero)
            Announce(expeditionUid, Loc.GetString("salvage-expedition-announcement-dungeon", ("direction", directionLocalization)));

        // Frontier: type-specific announcement
        switch (component.MissionParams.MissionType)
        {
            case SalvageMissionType.Destruction:
                if (TryComp<SalvageDestructionExpeditionComponent>(expeditionUid, out var destruction)
                    && destruction.Structures.Count > 0
                    && TryComp(destruction.Structures[0], out MetaDataComponent? structureMeta)
                    && structureMeta.EntityPrototype != null)
                {
                    var name = structureMeta.EntityPrototype.Name;
                    if (string.IsNullOrWhiteSpace(name))
                        name = Loc.GetString("salvage-expedition-announcement-destruction-entity-fallback");
                    // Assuming all structures are of the same type.
                    Announce(expeditionUid, Loc.GetString("salvage-expedition-announcement-destruction", ("structure", name), ("count", destruction.Structures.Count)));
                }
                break;
            case SalvageMissionType.Elimination:
                if (TryComp<SalvageEliminationExpeditionComponent>(expeditionUid, out var elimination)
                    && elimination.Megafauna.Count > 0
                    && TryComp(elimination.Megafauna[0], out MetaDataComponent? targetMeta)
                    && targetMeta.EntityPrototype != null)
                {
                    var name = targetMeta.EntityPrototype.Name;
                    if (string.IsNullOrWhiteSpace(name))
                        name = Loc.GetString("salvage-expedition-announcement-elimination-entity-fallback");
                    // Assuming all megafauna are of the same type.
                    Announce(expeditionUid, Loc.GetString("salvage-expedition-announcement-elimination", ("target", name), ("count", elimination.Megafauna.Count)));
                }
                break;
            default:
                break; // No announcement
        }
        // End Frontier

        component.Stage = ExpeditionStage.Running;
        Dirty(expeditionUid, component);
    }

    private void OnFTLStarted(ref FTLStartedEvent ev)
    {
        if (!TryGetExpeditionForEntity(ev.Entity, out var expeditionMapUid, out var expedition))
            return;

        // HardLight: only the wall SalvageExpeditionConsole flow keeps station-side
        // expedition data that needs CanFinish flipped. Disk-spawned expeditions store
        // a ShuttleConsoleComponent uid here and have no SalvageExpeditionDataComponent
        // to update, so the TryComp guard correctly no-ops them.
        if (expedition.Console != null && TryComp<SalvageExpeditionConsoleComponent>(expedition.Console.Value, out var consoleComp))
        {
            var data = GetStationExpeditionData(expedition.Console.Value);
            if (data != null)
            {
                data.CanFinish = false;
                UpdateConsole((expedition.Console.Value, consoleComp));
                Log.Info($"FTL started - disabled finishing for console {ToPrettyString(expedition.Console.Value)}");
            }
        }

        if (HasComp<ExpeditionParticipantShuttleComponent>(ev.Entity))
            RemComp<ExpeditionParticipantShuttleComponent>(ev.Entity);

        if (HasExpeditionParticipantShuttlesOnMap(expeditionMapUid))
            return;

        // Last shuttle has left so finish the mission.
        if (Exists(expeditionMapUid))
        {
            // HardLight: Clean up console state before deleting expedition
            CleanupExpeditionConsoleState(expeditionMapUid);
            QueueDel(expeditionMapUid);
        }
    }

    // Runs the expedition
    private void UpdateRunner()
    {
        // Generic missions
        var query = EntityQueryEnumerator<SalvageExpeditionComponent>();

        // Run the basic mission timers (e.g. announcements, auto-FTL, completion, etc)
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.EndTime == null)
                continue;

            var remaining = comp.EndTime.Value - _timing.CurTime;
            var audioLength = _audio.GetAudioLength(comp.SelectedSong);

            if (comp.Stage < ExpeditionStage.FinalCountdown && remaining < TimeSpan.FromSeconds(45))
            {
                comp.Stage = ExpeditionStage.FinalCountdown;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-seconds", ("duration", TimeSpan.FromSeconds(45).Seconds)));
            }
            else if (comp.Stage < ExpeditionStage.MusicCountdown && remaining < audioLength) // Frontier
            {
                // Frontier: handled client-side.
                // var audio = _audio.PlayPvs(comp.Sound, uid);
                // comp.Stream = audio?.Entity;
                // _audio.SetMapAudio(audio);
                // End Frontier
                comp.Stage = ExpeditionStage.MusicCountdown;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", audioLength.Minutes)));
            }
            else if (comp.Stage < ExpeditionStage.Countdown && remaining < TimeSpan.FromMinutes(5)) // Frontier: 4<5
            {
                comp.Stage = ExpeditionStage.Countdown;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", TimeSpan.FromMinutes(5).Minutes)));
            }
            // Auto-FTL out any shuttles
            else if (remaining < TimeSpan.FromSeconds(_shuttle.DefaultStartupTime) + TimeSpan.FromSeconds(0.5))
            {
                var ftlTime = (float) remaining.TotalSeconds;

                if (remaining < TimeSpan.FromSeconds(_shuttle.DefaultStartupTime))
                {
                    ftlTime = MathF.Max(0, (float) remaining.TotalSeconds - 0.5f);
                }

                ftlTime = MathF.Min(ftlTime, _shuttle.DefaultStartupTime);
                var shuttleQuery = EntityQueryEnumerator<ShuttleComponent, TransformComponent, ExpeditionParticipantShuttleComponent>();

                if (!TryGetExpeditionReturnMap(out var returnMapUid, out var targetSource)) // HardLight
                {
                    Log.Error($"Could not resolve expedition return map (DefaultMap or ColComm) for expedition {uid}; shuttles may be stuck.");
                    continue;
                }

                var targetMapId = Comp<MapComponent>(returnMapUid).MapId;
                var existingPositions = GetExistingGridPositions(targetMapId);

                // HardLight: FTL all shuttles on the expedition map, regardless of station component
                // This ensures shuttles get sent home even with the new console system
                while (shuttleQuery.MoveNext(out var shuttleUid, out var shuttle, out var shuttleXform, out _))
                {
                    if (!IsEntityOnExpedition(shuttleUid, uid, shuttleXform) || HasComp<FTLComponent>(shuttleUid))
                        continue;

                    var dropLocation = PickExpeditionReturnDropLocation(existingPositions); // HardLight

                    _shuttle.FTLToCoordinates(shuttleUid, shuttle, new EntityCoordinates(returnMapUid, dropLocation), 0f, ftlTime, TravelTime); // mapUid.Value<returnMapUid
                    Log.Info($"Normal timeout: FTLing shuttle {shuttleUid} home from expedition {uid} via {targetSource}"); // Added via {targetSource}
                    // End Frontier:  try to find a potential destination for ship that doesn't collide with other grids.
                }
            }

            if (remaining < TimeSpan.Zero)
            {
                if (comp.ReturnTriggered) // HardLight
                    continue;

                comp.ReturnTriggered = true; // HardLight

                // HardLight: Mission ended; FTL all shuttles out immediately before cleanup.
                FTLAllShuttlesHome(uid, 10f); // 10s travel time on forced end

                // Clean up console state; delete only after no shuttles remain on expedition map. // HardLight: Reworded
                CleanupExpeditionConsoleState(uid);
                QueueExpeditionDeletionWhenEmpty(uid); // HardLight
            }
        }

        // Frontier: mission-specific logic
        // Destruction
        var structureQuery = EntityQueryEnumerator<SalvageDestructionExpeditionComponent, SalvageExpeditionComponent>();

        while (structureQuery.MoveNext(out var uid, out var structure, out var comp))
        {
            if (comp.Completed)
                continue;

            var structureAnnounce = false;

            for (var i = structure.Structures.Count - 1; i >= 0; i--)
            {
                var objective = structure.Structures[i];

                if (Deleted(objective))
                {
                    structure.Structures.RemoveAt(i);
                    structureAnnounce = true;
                }
            }

            if (structureAnnounce)
                Announce(uid, Loc.GetString("salvage-expedition-structure-remaining", ("count", structure.Structures.Count)));

            if (structure.Structures.Count == 0)
            {
                comp.Completed = true;
                Announce(uid, Loc.GetString("salvage-expedition-completed"));
            }
        }

        // Elimination
        var eliminationQuery = EntityQueryEnumerator<SalvageEliminationExpeditionComponent, SalvageExpeditionComponent>();
        while (eliminationQuery.MoveNext(out var uid, out var elimination, out var comp))
        {
            if (comp.Completed)
                continue;

            var announce = false;

            for (var i = elimination.Megafauna.Count - 1; i >= 0; i--)
            {
                var mob = elimination.Megafauna[i];

                if (Deleted(mob) || _mobState.IsDead(mob))
                {
                    elimination.Megafauna.RemoveAt(i);
                    announce = true;
                }
            }

            if (announce)
                Announce(uid, Loc.GetString("salvage-expedition-megafauna-remaining", ("count", elimination.Megafauna.Count)));

            if (elimination.Megafauna.Count == 0)
            {
                comp.Completed = true;
                Announce(uid, Loc.GetString("salvage-expedition-completed"));
            }
        }
        // End Frontier: mission-specific logic
    }

    // HardLight: Clean up console state when expedition ends.
    //
    // There are two ways an expedition can be started, and they store different things
    // in `SalvageExpeditionComponent.Console`:
    //
    //  1. Wall SalvageExpeditionConsole (legacy/station path) -> `Console` is the
    //     wall console entity. There is per-station expedition data to reset, missions
    //     to clear, and a UI to repaint.
    //  2. Expedition disk inserted into a ShuttleConsole (current shuttle path)
    //     -> `Console` is the shuttle console entity. There is no station expedition
    //     data tied to it; the disk owns its own cooldown and the shuttle console UI
    //     does not need a reset. Cleanup is a no-op.
    //
    // The genuinely-bad cases are: `Console` is null (someone forgot to set it on a
    // new spawn path), or `Console` points to an entity that is neither type (we are
    // looking at the wrong entity). Both still log Warning so they get noticed.
    private void CleanupExpeditionConsoleState(EntityUid expeditionUid)
    {
        if (!TryComp<SalvageExpeditionComponent>(expeditionUid, out var component))
            return;

        if (component.Console == null)
        {
            // Player can sell the ship / round can restart while the expedition is still
            // running, in which case the console reference legitimately gets cleared.
            Log.Debug($"Skipping console cleanup for expedition {expeditionUid} - no originating console recorded.");
            return;
        }

        var consoleUid = component.Console.Value;

        // Disk-on-shuttle-console flow: nothing to clean up.
        if (HasComp<ShuttleConsoleComponent>(consoleUid) && !HasComp<SalvageExpeditionConsoleComponent>(consoleUid))
            return;

        if (!TryComp<SalvageExpeditionConsoleComponent>(consoleUid, out var consoleComp))
        {
            // Either the wall console was deleted between spawn and cleanup (sold ship,
            // deconstructed, etc.) or some other path stored a reference we don't
            // recognise. Either way there is nothing actionable here.
            Log.Debug($"Skipping console cleanup for expedition {expeditionUid} - originating console {consoleUid} is no longer a salvage console.");
            return;
        }

        var data = GetStationExpeditionData(consoleUid);
        if (data == null)
            return;

        Log.Info($"Cleaning up expedition state for console {ToPrettyString(consoleUid)}");

        // Reset station expedition state immediately
        data.ActiveMission = 0;
        data.CanFinish = false;
        data.Cooldown = false;
        // HardLight: Clear missions immediately to prevent UI confusion
        data.Missions.Clear();

        // Update console to show cleared state
        UpdateConsole((consoleUid, consoleComp));

        // HardLight: Generate new missions after a shorter delay to reduce confusion
        RobustTimer.Spawn(TimeSpan.FromSeconds(0.5), () => // consoleUid.SpawnTimer<RobustTimer.Spawn
        {
            if (Exists(consoleUid) && TryComp<SalvageExpeditionConsoleComponent>(consoleUid, out var comp))
            {
                var stationData = GetStationExpeditionData(consoleUid);
                if (stationData != null && !stationData.GeneratingMissions)
                {
                    GenerateMissions(stationData);
                    UpdateConsole((consoleUid, comp));
                    Log.Info($"Console {ToPrettyString(consoleUid)} missions regenerated after expedition cleanup");
                }
            }
        });

        Log.Info($"Console {ToPrettyString(consoleUid)} state reset successfully");
    }

    /// <summary>
    /// HardLight: Resolve a return destination map for expedition egress.
    /// Prefers the round's DefaultMap, then falls back to ColComm.
    /// </summary>
    private bool TryGetExpeditionReturnMap(out EntityUid targetMapUid, out string targetSource)
    {
        targetMapUid = EntityUid.Invalid;
        targetSource = string.Empty;

        var defaultMapId = _gameTicker.DefaultMap;
        if (_mapSystem.TryGetMap(defaultMapId, out var defaultMapUid) && defaultMapUid != null && defaultMapUid != EntityUid.Invalid)
        {
            targetMapUid = (EntityUid) defaultMapUid;
            targetSource = "DefaultMap";
            return true;
        }

        var colcommQuery = AllEntityQuery<StationColcommComponent>();
        if (!colcommQuery.MoveNext(out var colcommComp))
            return false;

        if (colcommComp.MapEntity != null && Exists(colcommComp.MapEntity.Value))
        {
            targetMapUid = colcommComp.MapEntity.Value;
            targetSource = "ColComm.MapEntity";
            return true;
        }

        if (colcommComp.Entity != null && Exists(colcommComp.Entity.Value))
        {
            var colcommXform = Transform(colcommComp.Entity.Value);
            if (colcommXform.MapUid != null && Exists(colcommXform.MapUid.Value))
            {
                targetMapUid = colcommXform.MapUid.Value;
                targetSource = "ColComm.GridMap";
                return true;
            }
        }

        return false;
    }

    private List<Vector2> GetExistingGridPositions(MapId mapId) // HardLight
    {
        var positions = new List<Vector2>();
        var gridQuery = EntityManager.AllEntityQueryEnumerator<MapGridComponent, TransformComponent>();

        while (gridQuery.MoveNext(out var _, out var _, out var xform))
        {
            if (xform.MapID == mapId)
                positions.Add(_transform.GetWorldPosition(xform));
        }

        return positions;
    }

    private Vector2 PickExpeditionReturnDropLocation(List<Vector2> existingPositions) // HardLight
    {
        var minDistanceSquared = ExpeditionReturnMinDistance * ExpeditionReturnMinDistance;
        var dropLocation = _random.NextVector2(ExpeditionReturnMinRange, ExpeditionReturnMaxRange);

        for (var i = 0; i < ExpeditionReturnPositionRetries; i++)
        {
            var valid = true;

            foreach (var pos in existingPositions)
            {
                if ((pos - dropLocation).LengthSquared() < minDistanceSquared)
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
                break;

            dropLocation = _random.NextVector2(ExpeditionReturnMinRange, ExpeditionReturnMaxRange);
        }

        // Reserve this location so subsequent shuttles in the same batch spread out.
        existingPositions.Add(dropLocation);
        return dropLocation;
    }

    private bool HasExpeditionParticipantShuttlesOnMap(EntityUid expeditionMapUid)
    {
        var shuttleQuery = EntityQueryEnumerator<ShuttleComponent, TransformComponent, ExpeditionParticipantShuttleComponent>();

        while (shuttleQuery.MoveNext(out var shuttleUid, out _, out var shuttleXform, out _))
        {
            if (IsEntityOnExpedition(shuttleUid, expeditionMapUid, shuttleXform))
                return true;
        }

        return false;
    }

    /// <summary>
    /// HardLight: FTL all shuttles currently on an expedition map back to the home map.
    /// </summary>
    /// <param name="expeditionMapUid">Map entity containing the expedition.</param>
    /// <param name="hyperspaceTime">Optional travel time override.</param>
    private void FTLAllShuttlesHome(EntityUid expeditionMapUid, float? hyperspaceTime = null)
    {
        var shuttleQuery = EntityQueryEnumerator<ShuttleComponent, TransformComponent, ExpeditionParticipantShuttleComponent>();
        if (!TryGetExpeditionReturnMap(out var returnMapUid, out var targetSource))
        {
            Log.Error($"No valid return map found (DefaultMap or ColComm) for expedition FTL egress from {expeditionMapUid}.");
            return;
        }

        var targetMapId = Comp<MapComponent>(returnMapUid).MapId;
        var existingPositions = GetExistingGridPositions(targetMapId);

        while (shuttleQuery.MoveNext(out var shuttleUid, out var shuttle, out var shuttleXform, out _))
        {
            if (!IsEntityOnExpedition(shuttleUid, expeditionMapUid, shuttleXform) || HasComp<FTLComponent>(shuttleUid))
                continue;

            var dropLocation = PickExpeditionReturnDropLocation(existingPositions);

            _shuttle.FTLToCoordinates(shuttleUid, shuttle, new EntityCoordinates(returnMapUid, dropLocation), 0f, startupTime: _shuttle.DefaultStartupTime, hyperspaceTime: hyperspaceTime ?? _shuttle.DefaultTravelTime);
            Log.Info($"Expedition end: FTLing shuttle {shuttleUid} home from expedition {expeditionMapUid} via {targetSource}");
        }
    }

    /// <summary>
    /// HardLight: Deletes the expedition map once all shuttles have actually left it.
    /// Uses retries to avoid deleting the map while FTL startup is delayed.
    /// </summary>
    private void QueueExpeditionDeletionWhenEmpty(EntityUid expeditionMapUid, int attempt = 0)
    {
        if (!Exists(expeditionMapUid) || !TryComp<SalvageExpeditionComponent>(expeditionMapUid, out _))
            return;

        if (HasExpeditionParticipantShuttlesOnMap(expeditionMapUid))
        {
            if (attempt >= 24)
            {
                Log.Warning($"Expedition {expeditionMapUid} still has expedition participant shuttles after cleanup retries; skipping forced map deletion to avoid deleting active players.");
                return;
            }

            RobustTimer.Spawn(TimeSpan.FromSeconds(5), () => QueueExpeditionDeletionWhenEmpty(expeditionMapUid, attempt + 1));
            return;
        }

        QueueDel(expeditionMapUid);
        Log.Info($"Deleted expedition map {expeditionMapUid} after all shuttles left.");
    }
}
