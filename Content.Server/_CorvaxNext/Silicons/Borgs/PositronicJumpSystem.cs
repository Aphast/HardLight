using System.Linq;
using Content.Server.Mech.Systems;
using Content.Server.Silicons.Borgs;
using Content.Server.Silicons.Laws;
using Content.Server.SurveillanceCamera;
using Content.Server._CorvaxNext.Silicons.Borgs.Components;
using Content.Server._Mono.SpaceArtillery.Components;
using Content.Shared.Mech.Components;
using Content.Shared.Actions;
using Content.Shared.Mind;
using Content.Shared.PAI;
using Content.Shared.Popups;
using Content.Shared._CorvaxNext.Silicons.Borgs;
using Content.Shared.Silicons.Borgs.Components;
using Content.Shared.Silicons.Laws.Components;
using Content.Shared.Silicons.StationAi;
using Content.Shared.Turrets;
using Content.Shared.Verbs;
using Robust.Shared.GameStates;
using Robust.Shared.Containers;

namespace Content.Server._CorvaxNext.Silicons.Borgs;

public sealed class PositronicJumpSystem : EntitySystem
{
    [Dependency] private readonly ActionContainerSystem _actionContainer = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SiliconLawSystem _lawSystem = default!;
    [Dependency] private readonly MechSystem _mech = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    private const string ReturnToAiAction = "ActionBackToAi";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BorgBrainComponent, GetVerbsEvent<AlternativeVerb>>(OnBrainGetVerbs);
        SubscribeLocalEvent<StationAiTurretComponent, GetVerbsEvent<AlternativeVerb>>(OnTurretGetVerbs);
        SubscribeLocalEvent<BorgChassisComponent, GetVerbsEvent<AlternativeVerb>>(OnBorgChassisGetVerbs);
        SubscribeLocalEvent<SpaceArtilleryComponent, GetVerbsEvent<AlternativeVerb>>(OnSpaceArtilleryGetVerbs);
        SubscribeLocalEvent<PAIComponent, GetVerbsEvent<AlternativeVerb>>(OnPAIGetVerbs);
        SubscribeLocalEvent<SurveillanceCameraComponent, SurveillanceCameraAlternativeVerbsEvent>(OnCameraGetVerbs);
        SubscribeLocalEvent<MechComponent, MechAlternativeVerbsEvent>(OnMechGetVerbs);
        SubscribeLocalEvent<PositronicReturnComponent, ComponentShutdown>(OnPositronicReturnShutdown);
        SubscribeLocalEvent<PositronicReturnComponent, ReturnMindIntoAiEvent>(OnReturnMindIntoAi);
        SubscribeLocalEvent<RemoteMechPilotComponent, EntGotRemovedFromContainerMessage>(OnRemoteMechPilotRemoved);
        SubscribeLocalEvent<RemoteMechPilotComponent, ComponentShutdown>(OnRemoteMechPilotShutdown);
    }

    private void OnBrainGetVerbs(EntityUid uid, BorgBrainComponent component, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.User == uid)
        {
            AddReturnVerb(uid, ref args);
            return;
        }

        if (!args.CanAccess)
            return;

        if (!ActorCanUseAbility(args.User))
            return;

        if (_mind.TryGetMind(uid, out _, out _))
            return;

        var user = args.User;
        var target = uid;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Take control",
            Act = () => TryTakeControl(user, target)
        });
    }

    private void OnTurretGetVerbs(EntityUid uid, StationAiTurretComponent component, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.User == uid)
        {
            AddReturnVerb(uid, ref args);
            return;
        }

        if (!args.CanAccess)
            return;

        if (!ActorCanUseAbility(args.User))
            return;

        if (_mind.TryGetMind(uid, out _, out _))
            return;

        var user = args.User;
        var target = uid;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Take control",
            Act = () => TryTakeControl(user, target)
        });
    }

    private void OnBorgChassisGetVerbs(EntityUid uid, BorgChassisComponent component, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess)
            return;

        if (!ActorCanUseAbility(args.User))
            return;

        if (_mind.TryGetMind(uid, out _, out _))
            return;

        var user = args.User;
        var target = uid;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Take control",
            Act = () => TryTakeControl(user, target)
        });
    }

    private void OnSpaceArtilleryGetVerbs(EntityUid uid, SpaceArtilleryComponent component, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess)
            return;

        if (!ActorCanUseAbility(args.User))
            return;

        if (_mind.TryGetMind(uid, out _, out _))
            return;

        var user = args.User;
        var target = uid;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Take control",
            Act = () => TryTakeControl(user, target)
        });
    }

    private void OnPAIGetVerbs(EntityUid uid, PAIComponent component, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.User == uid)
        {
            AddReturnVerb(uid, ref args);
            return;
        }

        if (!args.CanAccess)
            return;

        if (!ActorCanUseAbility(args.User))
            return;

        if (_mind.TryGetMind(uid, out _, out _))
            return;

        var user = args.User;
        var target = uid;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Take control",
            Act = () => TryTakeControl(user, target)
        });
    }

    private void OnCameraGetVerbs(EntityUid uid, SurveillanceCameraComponent component, ref SurveillanceCameraAlternativeVerbsEvent args)
    {
        var verbs = args.Args;

        if (verbs.User == uid)
        {
            AddReturnVerb(uid, ref verbs);
            return;
        }

        if (!verbs.CanAccess)
            return;

        if (!ActorCanUseAbility(verbs.User))
            return;

        if (_mind.TryGetMind(uid, out _, out _))
            return;

        var user = verbs.User;
        var target = uid;

        verbs.Verbs.Add(new AlternativeVerb
        {
            Text = "Take control",
            Act = () => TryTakeControl(user, target)
        });
    }

    private void OnMechGetVerbs(EntityUid uid, MechComponent component, ref MechAlternativeVerbsEvent args)
    {
        var verbs = args.Args;

        if (!ActorCanUseAbility(verbs.User))
            return;

        if (_mind.TryGetMind(uid, out _, out _))
            return;

        if (!_mech.IsEmpty(component))
            return;

        var user = verbs.User;
        var target = uid;

        verbs.Verbs.Add(new AlternativeVerb
        {
            Text = "Take control",
            Act = () => TryTakeControl(user, target)
        });
    }

    private bool ActorCanUseAbility(EntityUid user)
    {
        return HasComp<StationAiHeldComponent>(user)
               || HasComp<BorgChassisComponent>(user)
               || HasComp<StationAiTurretComponent>(user)
               || HasComp<BorgBrainComponent>(user)
               || HasComp<PAIComponent>(user);
    }

    private void AddReturnVerb(EntityUid uid, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!TryComp<PositronicReturnComponent>(uid, out var returnComp)
            || returnComp.ReturnTarget == null)
        {
            return;
        }

        var returnTarget = ResolveReturnOrigin(uid);

        if (returnTarget == uid)
            return;

        if (!ReturnTargetAvailable(returnTarget))
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Return control",
            Act = () => TryReturnControl(uid)
        });
    }

    private bool ReturnTargetAvailable(EntityUid returnTarget)
    {
        if (!Exists(returnTarget))
            return false;

        if (_mind.TryGetMind(returnTarget, out _, out _))
            return false;

        return true;
    }

    private EntityUid ResolveReturnOrigin(EntityUid entity)
    {
        var resolved = entity;
        var visited = new HashSet<EntityUid>();

        while (visited.Add(resolved)
               && TryComp<PositronicReturnComponent>(resolved, out var returnComp)
               && returnComp.ReturnTarget is { } next
               && next.IsValid()
               && Exists(next))
        {
            resolved = next;
        }

        return resolved;
    }

    private void TransferReturnState(EntityUid from, EntityUid to)
    {
        var returnTarget = ResolveReturnOrigin(from);
        var returnComp = EnsureComp<PositronicReturnComponent>(to);
        returnComp.ReturnTarget = returnTarget;
        _actions.AddAction(to, ref returnComp.ReturnActionEntity, ReturnToAiAction);

        if (!TryComp<PositronicReturnComponent>(from, out var previousReturnComp))
            return;

        RemoveReturnAction(previousReturnComp);
        RemComp<PositronicReturnComponent>(from);
    }

    private void OnReturnMindIntoAi(EntityUid uid, PositronicReturnComponent component, ref ReturnMindIntoAiEvent args)
    {
        if (TryReturnControl(uid))
        {
            args.Handled = true;
        }
    }

    private void OnPositronicReturnShutdown(EntityUid uid, PositronicReturnComponent component, ref ComponentShutdown args)
    {
        RemoveReturnAction(component);
    }

    public bool TryTakeControl(EntityUid user, EntityUid target)
    {
        if (TryComp<MechComponent>(target, out var mechComponent))
            return TryTakeMechControl(user, target, mechComponent);

        if (!_mind.TryGetMind(user, out var mindId, out var mind))
            return false;

        if (_mind.TryGetMind(target, out _, out _))
            return false;

        if (mind.OwnedEntity == target)
            return false;

        if (mind.OwnedEntity != null)
            TransferReturnState(mind.OwnedEntity.Value, target);

        _lawSystem.CopyLawsProvider(user, target);
        _mind.TransferTo(mindId, target, ghostCheckOverride: true, mind: mind);
        return true;
    }

    public bool TryReturnControl(EntityUid target)
    {
        if (TryComp<RemoteMechPilotComponent>(target, out var remoteMechPilot))
            return TryReturnMechControl(target, remoteMechPilot);

        if (!_mind.TryGetMind(target, out var mindId, out var mind))
            return false;

        if (!TryComp<PositronicReturnComponent>(target, out var returnComp)
            || returnComp.ReturnTarget == null)
        {
            return false;
        }

        var returnTarget = ResolveReturnOrigin(target);

        if (returnTarget == target)
            return false;

        if (!ReturnTargetAvailable(returnTarget))
            return false;

        RemoveReturnAction(returnComp);

        _mind.TransferTo(mindId, returnTarget, ghostCheckOverride: true, mind: mind);
        RemComp<PositronicReturnComponent>(target);
        return true;
    }

    private bool TryTakeMechControl(EntityUid user, EntityUid mech, MechComponent mechComponent)
    {
        if (!_mind.TryGetMind(user, out var mindId, out var mind))
        {
            PopupJumpFailure(user, "ai-remote-control-failed");
            return false;
        }

        if (_mind.TryGetMind(mech, out _, out _))
        {
            PopupJumpFailure(user, "ai-remote-control-mech-occupied");
            return false;
        }

        if (mind.OwnedEntity == null)
        {
            PopupJumpFailure(user, "ai-remote-control-failed");
            return false;
        }

        if (!_mech.IsEmpty(mechComponent))
        {
            PopupJumpFailure(user, "ai-remote-control-mech-occupied");
            return false;
        }

        var previous = mind.OwnedEntity.Value;
        var proxy = EntityManager.SpawnEntity(null, Transform(mech).Coordinates);
        var remotePilot = EnsureComp<RemoteMechPilotComponent>(proxy);
        remotePilot.Mech = mech;

        if (!_mech.TryInsert(mech,
                proxy,
                mechComponent,
                whitelistUser: user,
                bypassMovementCheck: true,
                bypassPilotWhitelist: true))
        {
            Del(proxy);
            PopupJumpFailure(user, "ai-remote-control-mech-failed");
            return false;
        }

        TransferReturnState(previous, proxy);

        if (mechComponent.MechEjectActionEntity != null)
        {
            _actions.RemoveAction(proxy, mechComponent.MechEjectActionEntity);
            mechComponent.MechEjectActionEntity = null;
        }

        _lawSystem.CopyLawsProvider(user, proxy);
        _mind.TransferTo(mindId, proxy, ghostCheckOverride: true, mind: mind);
        return true;
    }

    private void PopupJumpFailure(EntityUid user, string locKey)
    {
        _popup.PopupEntity(Loc.GetString(locKey), user, user);
    }

    private bool TryReturnMechControl(EntityUid proxy, RemoteMechPilotComponent remotePilot)
    {
        if (!_mind.TryGetMind(proxy, out var mindId, out var mind))
            return false;

        if (!TryComp<PositronicReturnComponent>(proxy, out var returnComp)
            || returnComp.ReturnTarget == null)
        {
            return false;
        }

        var returnTarget = ResolveReturnOrigin(proxy);

        if (returnTarget == proxy)
            return false;

        if (!ReturnTargetAvailable(returnTarget))
            return false;

        remotePilot.Returning = true;

        if (Exists(remotePilot.Mech)
            && TryComp<MechComponent>(remotePilot.Mech, out var mechComponent)
            && mechComponent.PilotSlot.ContainedEntity == proxy)
        {
            _mech.TryEject(remotePilot.Mech, mechComponent);
        }

        RemoveReturnAction(returnComp);

        _mind.TransferTo(mindId, returnTarget, ghostCheckOverride: true, mind: mind);
        Del(proxy);
        return true;
    }

    private void OnRemoteMechPilotRemoved(EntityUid uid, RemoteMechPilotComponent component, ref EntGotRemovedFromContainerMessage args)
    {
        if (component.Returning)
            return;

        if (args.Container.Owner != component.Mech)
            return;

        if (_mind.TryGetMind(uid, out _, out _))
            TryReturnControl(uid);
        else
            Del(uid);
    }

    private void OnRemoteMechPilotShutdown(EntityUid uid, RemoteMechPilotComponent component, ref ComponentShutdown args)
    {
        if (component.Mech == EntityUid.Invalid || !Exists(component.Mech))
            return;

        _container.RemoveEntity(component.Mech, uid, reparent: false, force: true);
    }

    private void RemoveReturnAction(PositronicReturnComponent component)
    {
        if (component.ReturnActionEntity is not { } actionId || !actionId.IsValid())
        {
            component.ReturnActionEntity = null;
            return;
        }

        if (Exists(actionId))
        {
            _actionContainer.RemoveAction(actionId);
            QueueDel(actionId);
        }

        component.ReturnActionEntity = null;
    }

    private void RewriteLaws(EntityUid from, EntityUid to)
    {
        _lawSystem.CopyLawsProvider(from, to);
    }
}
