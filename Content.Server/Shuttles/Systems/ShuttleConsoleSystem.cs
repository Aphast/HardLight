#nullable enable
using Content.Server._Mono.Ships.Systems; // Mono
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared._Mono.Ships.Components; // Mono
using Content.Shared._NF.Shuttles.Events; // Frontier
using Content.Shared.ActionBlocker;
using Content.Shared.Alert;
using Content.Shared.Popups;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Events;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Tag;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Movement.Systems;
using Content.Shared.Power;
using Content.Shared.Shuttles.UI.MapObjects;
using Content.Shared.Timing;
using Robust.Server.GameObjects;
using Robust.Shared.Collections;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Utility;
using Content.Shared.UserInterface;
using Robust.Shared.Prototypes;
using Content.Shared.Access.Systems; // Frontier
using Content.Shared.Construction.Components; // Frontier
using Content.Server.Salvage.Expeditions;
using Content.Shared.Procedural;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Salvage.Expeditions.Modifiers;
using Robust.Shared.Audio.Systems;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleConsoleSystem : SharedShuttleConsoleSystem
{
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly AlertsSystem _alertsSystem = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedContentEyeSystem _eyeSystem = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly Robust.Shared.Timing.IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly Content.Server.Salvage.SalvageSystem _salvage = default!;
    [Dependency] private readonly ExpeditionDiskSystem _expeditionDisks = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!; // HL
    [Dependency] private readonly CrewedShuttleSystem _crewedShuttle = default!; // Mono

    private EntityQuery<MetaDataComponent> _metaQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private readonly HashSet<Entity<ShuttleConsoleComponent>> _consoles = new();

    private static readonly ProtoId<TagPrototype> CanPilotTag = "CanPilot";
    private static readonly TimeSpan ExpeditionConsoleCooldown = TimeSpan.FromMinutes(15);

    public override void Initialize()
    {
        base.Initialize();

        _metaQuery = GetEntityQuery<MetaDataComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        SubscribeLocalEvent<ShuttleConsoleComponent, ComponentShutdown>(OnConsoleShutdown);
        SubscribeLocalEvent<ShuttleConsoleComponent, PowerChangedEvent>(OnConsolePowerChange);
        SubscribeLocalEvent<ShuttleConsoleComponent, AnchorStateChangedEvent>(OnConsoleAnchorChange);
        SubscribeLocalEvent<ShuttleConsoleComponent, EntInsertedIntoContainerMessage>(OnConsoleDiskSlotChanged);
        SubscribeLocalEvent<ShuttleConsoleComponent, EntRemovedFromContainerMessage>(OnConsoleDiskSlotChanged);
        SubscribeLocalEvent<ShuttleConsoleComponent, ActivatableUIOpenAttemptEvent>(OnConsoleUIOpenAttempt);
        Subs.BuiEvents<ShuttleConsoleComponent>(ShuttleConsoleUiKey.Key, subs =>
        {
            subs.Event<ShuttleConsoleFTLBeaconMessage>(OnBeaconFTLMessage);
            subs.Event<ShuttleConsoleFTLPositionMessage>(OnPositionFTLMessage);
            subs.Event<ShuttleConsoleFTLStationDockMessage>(OnStationDockFTLMessage);
            subs.Event<ShuttleConsoleExpeditionDiskActivateMessage>(OnExpeditionDiskActivateMessage);
            subs.Event<ShuttleConsoleExpeditionEndMessage>(OnExpeditionEndMessage);
            subs.Event<ShuttleConsoleWEPMessage>(OnWEPMessage); // HL
            subs.Event<BoundUIClosedEvent>(OnConsoleUIClose);
        });

        SubscribeLocalEvent<DroneConsoleComponent, ConsoleShuttleEvent>(OnCargoGetConsole);
        SubscribeLocalEvent<DroneConsoleComponent, AfterActivatableUIOpenEvent>(OnDronePilotConsoleOpen);
        Subs.BuiEvents<DroneConsoleComponent>(ShuttleConsoleUiKey.Key, subs =>
        {
            subs.Event<BoundUIClosedEvent>(OnDronePilotConsoleClose);
        });

        SubscribeLocalEvent<DockEvent>(OnDock);
        SubscribeLocalEvent<UndockEvent>(OnUndock);

        SubscribeLocalEvent<PilotComponent, ComponentGetState>(OnGetState);
        SubscribeLocalEvent<PilotComponent, StopPilotingAlertEvent>(OnStopPilotingAlert);

        SubscribeLocalEvent<FTLDestinationComponent, ComponentStartup>(OnFtlDestStartup);
        SubscribeLocalEvent<FTLDestinationComponent, ComponentShutdown>(OnFtlDestShutdown);

        InitializeFTL();

        InitializeNFDrone(); // Frontier: add our drone subscriptions
    }

    private void OnFtlDestStartup(EntityUid uid, FTLDestinationComponent component, ComponentStartup args)
    {
        RefreshShuttleConsoles();
    }

    private void OnFtlDestShutdown(EntityUid uid, FTLDestinationComponent component, ComponentShutdown args)
    {
        RefreshShuttleConsoles();
    }

    private void OnDock(DockEvent ev)
    {
        RefreshShuttleConsoles();
    }

    private void OnUndock(UndockEvent ev)
    {
        RefreshShuttleConsoles();
    }

    /// <summary>
    /// Refreshes all the shuttle console data for a particular grid.
    /// </summary>
    public void RefreshShuttleConsoles(EntityUid gridUid)
    {
        var exclusions = new List<ShuttleExclusionObject>();
        GetExclusions(ref exclusions);
        _consoles.Clear();
        _lookup.GetChildEntities(gridUid, _consoles);
        DockingInterfaceState? dockState = null;

        foreach (var entity in _consoles)
        {
            UpdateState(entity, ref dockState);
        }
    }

    /// <summary>
    /// Refreshes all of the data for shuttle consoles.
    /// </summary>
    public void RefreshShuttleConsoles()
    {
        var exclusions = new List<ShuttleExclusionObject>();
        GetExclusions(ref exclusions);
        var query = AllEntityQuery<ShuttleConsoleComponent>();
        DockingInterfaceState? dockState = null;

        while (query.MoveNext(out var uid, out _))
        {
            UpdateState(uid, ref dockState);
        }
    }

    /// <summary>
    /// Stop piloting if the window is closed.
    /// </summary>
    private void OnConsoleUIClose(EntityUid uid, ShuttleConsoleComponent component, BoundUIClosedEvent args)
    {
        if ((ShuttleConsoleUiKey)args.UiKey != ShuttleConsoleUiKey.Key)
        {
            return;
        }

        RemovePilot(args.Actor);
    }

    private void OnExpeditionDiskActivateMessage(Entity<ShuttleConsoleComponent> ent, ref ShuttleConsoleExpeditionDiskActivateMessage args)
    {
        if (_timing.CurTime < ent.Comp.ExpeditionCooldownEnd)
        {
            var remaining = ent.Comp.ExpeditionCooldownEnd - _timing.CurTime;
            _popup.PopupEntity(Loc.GetString("shuttle-console-expedition-disk-cooldown", ("time", remaining.ToString("hh\\:mm\\:ss"))), ent.Owner, PopupType.MediumCaution);
            return;
        }

        if (!TryComp<ItemSlotsComponent>(ent.Owner, out var slots) ||
            !_itemSlots.TryGetSlot(ent.Owner, SharedShuttleConsoleComponent.DiskSlotName, out var slot, component: slots) ||
            !slot.HasItem)
        {
            return;
        }
        EntityUid? diskUidNullable = slot.ContainerSlot?.ContainedEntity;
        if (diskUidNullable == null)
            return;

        var diskUid = diskUidNullable.Value;
        if (!TryComp(diskUid, out ExpeditionDiskComponent? diskComp))
            return;

        if (_expeditionDisks.TryActivateFromConsole(ent.Owner, diskUid, diskComp))
        {
            ent.Comp.ExpeditionCooldownEnd = _timing.CurTime + ExpeditionConsoleCooldown;
        }

        DockingInterfaceState? dockState = null;
        UpdateState(ent.Owner, ref dockState);
    }

    private void OnExpeditionEndMessage(Entity<ShuttleConsoleComponent> ent, ref ShuttleConsoleExpeditionEndMessage args)
    {
        if (_salvage.TryEndExpeditionEarlyFromConsole(ent.Owner))
        {
            DockingInterfaceState? dockState = null;
            UpdateState(ent.Owner, ref dockState);
            return;
        }

        _popup.PopupEntity(Loc.GetString("salvage-expedition-shuttle-not-found"), ent.Owner, PopupType.MediumCaution);
    }

    private void OnConsoleUIOpenAttempt(EntityUid uid, ShuttleConsoleComponent component,
        ActivatableUIOpenAttemptEvent args)
    {
        // Mono: on crewed shuttles, deny opening a shuttle console if this user already has
        // a gunnery console open on the same grid (unless they are an AdvancedPilot).
        var shuttle = _transform.GetParentUid(uid);
        var uiOpen = _crewedShuttle.AnyGunneryConsoleActiveByPlayer(shuttle, args.User);
        var forceOne = HasComp<CrewedShuttleComponent>(shuttle) && !HasComp<AdvancedPilotComponent>(args.User);

        if (uiOpen && forceOne)
        {
            args.Cancel();
            _popup.PopupClient(Loc.GetString("shuttle-console-crewed"), args.User);
            return;
        }

        if (!TryPilot(args.User, uid))
            args.Cancel();
    }

    private void OnConsoleAnchorChange(EntityUid uid, ShuttleConsoleComponent component,
        ref AnchorStateChangedEvent args)
    {
        DockingInterfaceState? dockState = null;
        UpdateState(uid, ref dockState);
    }

    private void OnConsoleDiskSlotChanged(EntityUid uid, ShuttleConsoleComponent component, ContainerModifiedMessage args)
    {
        if (!TryComp<ItemSlotsComponent>(uid, out var slots) ||
            !_itemSlots.TryGetSlot(uid, SharedShuttleConsoleComponent.DiskSlotName, out var slot, component: slots))
        {
            return;
        }

        if (slot.ContainerSlot == null || args.Container.ID != slot.ContainerSlot.ID)
            return;

        DockingInterfaceState? dockState = null;
        UpdateState(uid, ref dockState);
    }

    private void OnConsolePowerChange(EntityUid uid, ShuttleConsoleComponent component, ref PowerChangedEvent args)
    {
        DockingInterfaceState? dockState = null;
        UpdateState(uid, ref dockState);
        _shuttle.NfSetPowered(uid, component, args.Powered); // Frontier
    }

    private bool TryPilot(EntityUid user, EntityUid uid)
    {
        if (!_tags.HasTag(user, CanPilotTag) ||
            !TryComp<ShuttleConsoleComponent>(uid, out var component) ||
            !this.IsPowered(uid, EntityManager) ||
            !Transform(uid).Anchored ||
            !_blocker.CanInteract(user, uid))
        {
            return false;
        }

        if (!_access.IsAllowed(user, uid)) // Frontier: check access
            return false; // Frontier

        var pilotComponent = EnsureComp<PilotComponent>(user);
        var console = pilotComponent.Console;

        if (console != null)
        {
            RemovePilot(user, pilotComponent);

            // This feels backwards; is this intended to be a toggle?
            if (console == uid)
                return false;
        }

        AddPilot(uid, user, component);
        return true;
    }

    private void OnGetState(EntityUid uid, PilotComponent component, ref ComponentGetState args)
    {
        args.State = new PilotComponentState(GetNetEntity(component.Console));
    }

    private void OnStopPilotingAlert(Entity<PilotComponent> ent, ref StopPilotingAlertEvent args)
    {
        if (ent.Comp.Console != null)
        {
            RemovePilot(ent, ent);
        }
    }

    /// <summary>
    /// Returns the position and angle of all dockingcomponents.
    /// </summary>
    public Dictionary<NetEntity, List<DockingPortState>> GetAllDocks()
    {
        // TODO: NEED TO MAKE SURE THIS UPDATES ON ANCHORING CHANGES!
        var result = new Dictionary<NetEntity, List<DockingPortState>>();
        var query = AllEntityQuery<DockingComponent, TransformComponent, MetaDataComponent>();

        while (query.MoveNext(out var uid, out var comp, out var xform, out var metadata))
        {
            if (xform.ParentUid != xform.GridUid)
                continue;

            // Frontier: skip unanchored docks (e.g. portable gaslocks)
            if (HasComp<AnchorableComponent>(uid) && !xform.Anchored)
                continue;
            // End Frontier

            if (xform.GridUid is not { } gridUid)
                continue;

            var gridDocks = result.GetOrNew(GetNetEntity(gridUid));

            var state = new DockingPortState()
            {
                Name = metadata.EntityName,
                Coordinates = GetNetCoordinates(xform.Coordinates),
                Angle = xform.LocalRotation,
                Entity = GetNetEntity(uid),
                GridDockedWith =
                    _xformQuery.TryGetComponent(comp.DockedWith, out var otherDockXform) ?
                    GetNetEntity(otherDockXform.GridUid) :
                    null,
                LabelName = comp.Name != null ? Loc.GetString(comp.Name) : null, // Frontier: docking labels
                RadarColor = comp.RadarColor, // Frontier
                HighlightedRadarColor = comp.HighlightedRadarColor, // Frontier
                DockType = comp.DockType, // Frontier
                ReceiveOnly = comp.ReceiveOnly, // Frontier
            };

            gridDocks.Add(state);
        }

        return result;
    }

    private void UpdateState(EntityUid consoleUid, ref DockingInterfaceState? dockState)
    {
        EntityUid? entity = consoleUid;

        var getShuttleEv = new ConsoleShuttleEvent
        {
            Console = entity,
        };

        var entityUid = entity ?? consoleUid;
        RaiseLocalEvent(entityUid, ref getShuttleEv);
        entity = getShuttleEv.Console ?? entityUid;

        TryComp(entity, out TransformComponent? consoleXform);
        var shuttleGridUid = consoleXform?.GridUid;

        NavInterfaceState navState;
        ShuttleMapInterfaceState mapState;
        dockState ??= GetDockState();

        if (shuttleGridUid is { } shuttleGrid)
        {
            navState = GetNavState(entityUid, dockState.Docks);
            mapState = GetMapState(shuttleGrid);
        }
        else
        {
            navState = new NavInterfaceState(0f, null, null, new Dictionary<NetEntity, List<DockingPortState>>(), InertiaDampeningMode.Dampen, ServiceFlags.None); // Frontier: inertia dampening);
            mapState = new ShuttleMapInterfaceState(
                FTLState.Invalid,
                default,
                new List<ShuttleBeaconObject>(),
                new List<ShuttleExclusionObject>(),
                new List<ShuttleStationObject>());
        }

        if (_ui.HasUi(consoleUid, ShuttleConsoleUiKey.Key))
        {
            var expeditionState = GetExpeditionDiskState(consoleUid);
            // HL: include WEP state
            var wepActive = false;
            var wepCooldown = TimeSpan.Zero;
            if (shuttleGridUid is { } wepGrid && TryComp<ShuttleComponent>(wepGrid, out var wepShuttle))
            {
                wepActive = wepShuttle.WepBoostActive;
                wepCooldown = wepShuttle.WepCooldownExpiry;
            }
            _ui.SetUiState(consoleUid, ShuttleConsoleUiKey.Key, new ShuttleBoundUserInterfaceState(navState, mapState, dockState, expeditionState, wepActive, wepCooldown));
        }
    }

    private ExpeditionDiskInterfaceState GetExpeditionDiskState(EntityUid consoleUid)
    {
        var consoleXform = Transform(consoleUid);
        SalvageExpeditionComponent? expedition = null;
        var inExpedition = consoleXform.MapUid != null && TryComp(consoleXform.MapUid.Value, out expedition);
        var canEndExpedition = inExpedition && expedition != null && expedition.Stage >= ExpeditionStage.Running;

        if (!TryComp<ItemSlotsComponent>(consoleUid, out var slots) ||
            !_itemSlots.TryGetSlot(consoleUid, SharedShuttleConsoleComponent.DiskSlotName, out var slot, component: slots) ||
            !slot.HasItem)
        {
            return new ExpeditionDiskInterfaceState(false, string.Empty, 0, string.Empty, false, TimeSpan.Zero, false, inExpedition, canEndExpedition);
        }
        EntityUid? diskUidNullable = slot.ContainerSlot?.ContainedEntity;
        if (diskUidNullable == null)
        {
            return new ExpeditionDiskInterfaceState(false, string.Empty, 0, string.Empty, false, TimeSpan.Zero, false, inExpedition, canEndExpedition);
        }

        var diskUid = diskUidNullable.Value;
        if (!TryComp(diskUid, out ExpeditionDiskComponent? diskComp))
        {
            return new ExpeditionDiskInterfaceState(false, string.Empty, 0, string.Empty, false, TimeSpan.Zero, false, inExpedition, canEndExpedition);
        }

        var difficultyNumber = diskComp.DifficultyNumber;
        if (!_prototypeManager.TryIndex<SalvageDifficultyPrototype>(diskComp.Difficulty, out var difficultyProto))
        {
            var fallbackObjective = Loc.GetString($"salvage-expedition-type-{diskComp.MissionType}");
            var (onCooldown, remaining) = GetConsoleCooldownState(consoleUid, diskComp.CooldownEnd);
            return new ExpeditionDiskInterfaceState(true, Loc.GetString("shuttle-console-unknown"), difficultyNumber, fallbackObjective, onCooldown, remaining, !onCooldown, inExpedition, canEndExpedition);
        }

        var mission = _salvage.GetMission(diskComp.MissionType, difficultyProto, diskComp.Seed);
        var biomeProto = _prototypeManager.Index<SalvageBiomeModPrototype>(mission.Biome);
        var planet = string.IsNullOrWhiteSpace(Loc.GetString(biomeProto.Description))
            ? Loc.GetString(biomeProto.ID)
            : Loc.GetString(biomeProto.Description);

        var objective = Loc.GetString($"salvage-expedition-type-{diskComp.MissionType}");
        var (cooldown, cooldownRemaining) = GetConsoleCooldownState(consoleUid, diskComp.CooldownEnd);

        return new ExpeditionDiskInterfaceState(true, planet, difficultyNumber, objective, cooldown, cooldownRemaining, !cooldown, inExpedition, canEndExpedition);
    }

    private (bool OnCooldown, TimeSpan Remaining) GetConsoleCooldownState(EntityUid consoleUid, TimeSpan diskCooldownEnd)
    {
        var now = _timing.CurTime;
        var consoleCooldownEnd = TimeSpan.Zero;
        if (TryComp<ShuttleConsoleComponent>(consoleUid, out var consoleComp))
            consoleCooldownEnd = consoleComp.ExpeditionCooldownEnd;

        var diskRemaining = diskCooldownEnd > now ? diskCooldownEnd - now : TimeSpan.Zero;
        var consoleRemaining = consoleCooldownEnd > now ? consoleCooldownEnd - now : TimeSpan.Zero;
        var remaining = diskRemaining > consoleRemaining ? diskRemaining : consoleRemaining;
        return (remaining > TimeSpan.Zero, remaining);
    }

    // HL: WEP message handler
    private void OnWEPMessage(Entity<ShuttleConsoleComponent> ent, ref ShuttleConsoleWEPMessage args)
    {
        var xform = Transform(ent.Owner);
        if (xform.GridUid is not { } gridUid || !TryComp<ShuttleComponent>(gridUid, out var shuttle))
            return;

        var mover = EntityManager.System<Content.Server.Physics.Controllers.MoverController>();
        mover.ActivateWEP(gridUid, shuttle);
        // Note: OnWEPActivated is called by ActivateWEP on success.
    }

    private const float WepPowerDraw = 150_000f; // 150 kW peak recharge draw

    /// <summary>
    /// Called by MoverController after WEP successfully activates. Refreshes UI — power draw starts on expiry.
    /// </summary>
    public void OnWEPActivated(EntityUid gridUid)
    {
        RefreshShuttleConsoles();
    }

    private void AdjustWEPConsoleLoad(EntityUid gridUid, float delta)
    {
        var query = EntityQueryEnumerator<ShuttleConsoleComponent, ApcPowerReceiverComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var receiver, out var xform))
        {
            if (xform.GridUid == gridUid)
                receiver.Load += delta;
        }
    }
    // End HL

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // HL: expire WEP boosts, stop audio, restore power, refresh UI
        var wepQuery = EntityQueryEnumerator<ShuttleComponent>();
        while (wepQuery.MoveNext(out var gridUid, out var shuttle))
        {
            // Skip the vast majority of shuttles that have no active WEP state.
            // WepBoostActive flips on activation; WepPowerApplied stays true through the recharge ramp.
            if (!shuttle.WepBoostActive && !shuttle.WepPowerApplied)
                continue;

            // WEP boost expired — begin recharge ramp.
            if (shuttle.WepBoostActive && _timing.CurTime >= shuttle.WepBoostExpiry)
            {
                shuttle.WepBoostActive = false;
                shuttle.WepThrustMultiplier = 1f;
                shuttle.WepAudioStream = _audio.Stop(shuttle.WepAudioStream);
                shuttle.WepPowerApplied = true;
                shuttle.WepCurrentLoad = 0f;
                shuttle.WepLastLoadUpdateTime = _timing.CurTime;
                RefreshShuttleConsoles();
            }

            // Recharge ramp: step load up every second until cooldown ends.
            if (shuttle.WepPowerApplied)
            {
                if (_timing.CurTime >= shuttle.WepCooldownExpiry)
                {
                    // Cooldown done — remove remaining load and mark WEP ready.
                    if (shuttle.WepCurrentLoad > 0f)
                    {
                        AdjustWEPConsoleLoad(gridUid, -shuttle.WepCurrentLoad);
                        shuttle.WepCurrentLoad = 0f;
                    }
                    shuttle.WepPowerApplied = false;
                    RefreshShuttleConsoles();
                }
                else if (_timing.CurTime >= shuttle.WepLastLoadUpdateTime + TimeSpan.FromSeconds(1))
                {
                    shuttle.WepLastLoadUpdateTime = _timing.CurTime;
                    var elapsed = (float)(_timing.CurTime - shuttle.WepBoostExpiry).TotalSeconds;
                    var fraction = Math.Clamp(elapsed / ShuttleComponent.WepCooldownDuration, 0f, 1f);
                    var targetLoad = WepPowerDraw * fraction;
                    var delta = targetLoad - shuttle.WepCurrentLoad;
                    AdjustWEPConsoleLoad(gridUid, delta);
                    shuttle.WepCurrentLoad = targetLoad;
                }
            }
        }
        // End HL

        var toRemove = new ValueList<(EntityUid, PilotComponent)>();
        var query = EntityQueryEnumerator<PilotComponent>();

        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Console == null)
                continue;

            if (!_blocker.CanInteract(uid, comp.Console))
            {
                toRemove.Add((uid, comp));
            }
        }

        foreach (var (uid, comp) in toRemove)
        {
            RemovePilot(uid, comp);
        }
    }

    protected override void HandlePilotShutdown(EntityUid uid, PilotComponent component, ComponentShutdown args)
    {
        base.HandlePilotShutdown(uid, component, args);
        RemovePilot(uid, component);
    }

    private void OnConsoleShutdown(EntityUid uid, ShuttleConsoleComponent component, ComponentShutdown args)
    {
        ClearPilots(component);

        // HL: clean up WEP state so a destroyed console doesn't leave stuck audio or power load.
        var xform = Transform(uid);
        if (xform.GridUid is not { } gridUid || !TryComp<ShuttleComponent>(gridUid, out var shuttle))
            return;

        if (!shuttle.WepBoostActive && !shuttle.WepPowerApplied)
            return;

        shuttle.WepAudioStream = _audio.Stop(shuttle.WepAudioStream);
        shuttle.WepBoostActive = false;
        shuttle.WepThrustMultiplier = 1f;

        if (shuttle.WepCurrentLoad > 0f)
        {
            var loadQuery = EntityQueryEnumerator<ShuttleConsoleComponent, ApcPowerReceiverComponent, TransformComponent>();
            while (loadQuery.MoveNext(out var consoleUid, out _, out var receiver, out var consoleXform))
            {
                if (consoleXform.GridUid == gridUid && consoleUid != uid)
                    receiver.Load = MathF.Max(0f, receiver.Load - shuttle.WepCurrentLoad);
            }
            shuttle.WepCurrentLoad = 0f;
        }

        shuttle.WepPowerApplied = false;
        RefreshShuttleConsoles();
    }

    public void AddPilot(EntityUid uid, EntityUid entity, ShuttleConsoleComponent component)
    {
        if (!EntityManager.TryGetComponent(entity, out PilotComponent? pilotComponent)
        || component.SubscribedPilots.Contains(entity))
        {
            return;
        }

        _eyeSystem.SetZoom(entity, component.Zoom, ignoreLimits: true);

        component.SubscribedPilots.Add(entity);

        _alertsSystem.ShowAlert(entity, pilotComponent.PilotingAlert);

        pilotComponent.Console = uid;
        ActionBlockerSystem.UpdateCanMove(entity);
        pilotComponent.Position = EntityManager.GetComponent<TransformComponent>(entity).Coordinates;
        Dirty(entity, pilotComponent);
    }

    public void RemovePilot(EntityUid pilotUid, PilotComponent pilotComponent)
    {
        var console = pilotComponent.Console;

        if (!TryComp<ShuttleConsoleComponent>(console, out var helm))
            return;

        pilotComponent.Console = null;
        pilotComponent.Position = null;
        _eyeSystem.ResetZoom(pilotUid);

        if (!helm.SubscribedPilots.Remove(pilotUid))
            return;

        _alertsSystem.ClearAlert(pilotUid, pilotComponent.PilotingAlert);

        _popup.PopupEntity(Loc.GetString("shuttle-pilot-end"), pilotUid, pilotUid);

        if (pilotComponent.LifeStage < ComponentLifeStage.Stopping)
            EntityManager.RemoveComponent<PilotComponent>(pilotUid);
    }

    public void RemovePilot(EntityUid entity)
    {
        if (!EntityManager.TryGetComponent(entity, out PilotComponent? pilotComponent))
            return;

        RemovePilot(entity, pilotComponent);
    }

    public void ClearPilots(ShuttleConsoleComponent component)
    {
        var query = GetEntityQuery<PilotComponent>();
        while (component.SubscribedPilots.TryGetValue(0, out var pilot))
        {
            if (query.TryGetComponent(pilot, out var pilotComponent))
                RemovePilot(pilot, pilotComponent);
        }
    }

    /// <summary>
    /// Specific for a particular shuttle.
    /// </summary>
    public NavInterfaceState GetNavState(Entity<RadarConsoleComponent?, TransformComponent?> entity, Dictionary<NetEntity, List<DockingPortState>> docks)
    {
        if (!Resolve(entity, ref entity.Comp1, ref entity.Comp2))
            return new NavInterfaceState(SharedRadarConsoleSystem.DefaultMaxRange, null, null, docks, Shared._NF.Shuttles.Events.InertiaDampeningMode.Dampen, ServiceFlags.None); // Frontier: add inertia dampening

        return GetNavState(
            entity,
            docks,
            entity.Comp2.Coordinates,
            entity.Comp2.LocalRotation);
    }

    public NavInterfaceState GetNavState(
        Entity<RadarConsoleComponent?, TransformComponent?> entity,
        Dictionary<NetEntity, List<DockingPortState>> docks,
        EntityCoordinates coordinates,
        Angle angle)
    {
        if (!Resolve(entity, ref entity.Comp1, ref entity.Comp2))
            return new NavInterfaceState(SharedRadarConsoleSystem.DefaultMaxRange, GetNetCoordinates(coordinates), angle, docks, InertiaDampeningMode.Dampen, ServiceFlags.None); // Frontier: add inertial dampening

        return new NavInterfaceState(
            entity.Comp1.MaxRange,
            GetNetCoordinates(coordinates),
            angle,
            docks,
            _shuttle.NfGetInertiaDampeningMode(entity), // Frontier
            _shuttle.NfGetServiceFlags(entity)); // Frontier
    }

    /// <summary>
    /// Global for all shuttles.
    /// </summary>
    /// <returns></returns>
    public DockingInterfaceState GetDockState()
    {
        var docks = GetAllDocks();
        return new DockingInterfaceState(docks);
    }

    /// <summary>
    /// Specific to a particular shuttle.
    /// </summary>
    public ShuttleMapInterfaceState GetMapState(Entity<FTLComponent?> shuttle)
    {
        FTLState ftlState = FTLState.Available;
        StartEndTime stateDuration = default;

        if (Resolve(shuttle, ref shuttle.Comp, false) && shuttle.Comp.LifeStage < ComponentLifeStage.Stopped)
        {
            ftlState = shuttle.Comp.State;
            stateDuration = _shuttle.GetStateTime(shuttle.Comp);
        }

        List<ShuttleBeaconObject>? beacons = null;
        List<ShuttleExclusionObject>? exclusions = null;
        List<ShuttleStationObject>? stations = null;
        GetBeacons(ref beacons);
        //RemoveCurrentLavalandBeacons(shuttle.Owner, beacons);
        GetExclusions(ref exclusions);
        GetStations(ref stations);

        return new ShuttleMapInterfaceState(
            ftlState,
            stateDuration,
            beacons ?? new List<ShuttleBeaconObject>(),
            exclusions ?? new List<ShuttleExclusionObject>(),
            stations ?? new List<ShuttleStationObject>());
    }
}
