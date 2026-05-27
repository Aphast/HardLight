using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.Hypospray.Events;
using Content.Shared.Chemistry;
using Content.Shared.CombatMode;
using Content.Shared.Database;
using Content.Shared.FixedPoint;
using Content.Shared.Forensics;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Timing;
using Content.Shared.Weapons.Melee.Events;
using Content.Server.Body.Components;
using System.Linq;
using Robust.Server.Audio;
using Content.Shared.DoAfter; // Frontier
using Content.Shared._DV.Chemistry.Components; // Frontier
using Content.Shared.Inventory; // Hardlight
using Content.Shared.Popups; // Hardlight
using Content.Shared.Tag; // Hardlight
using Robust.Shared.Prototypes; // Hardlight

namespace Content.Server.Chemistry.EntitySystems;

public sealed class HypospraySystem : SharedHypospraySystem
{
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!; // Frontier - Upstream: #30704 - MIT
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly TagSystem _tag = default!; // Hardlight
    [Dependency] private readonly InventorySystem _inventory = default!; // Hardlight

    private static readonly ProtoId<TagPrototype> HardsuitTag = "Hardsuit"; // Hardlight

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HyposprayComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<HyposprayComponent, MeleeHitEvent>(OnAttack);
        SubscribeLocalEvent<HyposprayComponent, UseInHandEvent>(OnUseInHand);
        SubscribeLocalEvent<HyposprayComponent, HyposprayDoAfterEvent>(OnDoAfter); // Frontier - Upstream: #30704 - MIT
    }

    // Frontier - Upstream: #30704 - MIT
    private void OnDoAfter(Entity<HyposprayComponent> entity, ref HyposprayDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || args.Args.Target == null)
            return;

        args.Handled = TryDoInject(entity, args.Args.Target.Value, args.Args.User);
    }
    // End Frontier

    private bool TryUseHypospray(Entity<HyposprayComponent> entity, EntityUid target, EntityUid user)
    {
        // if target is ineligible but is a container, try to draw from the container
        if (!EligibleEntity(target, EntityManager, entity)
            && _solutionContainers.TryGetDrawableSolution(target, out var drawableSolution, out _))
        {
            return TryDraw(entity, target, drawableSolution.Value, user);
        }

        var component = entity.Comp;
        var injectTime = component.InjectTime;

        // Instant mode or non-mob target: inject immediately.
        if (injectTime == TimeSpan.Zero || !HasComp<MobStateComponent>(target))
            return TryDoInject(entity, target, user);

        if (!InjectionFailureCheck(entity, target, user, out _, out _, out _, out _))
            return true;

        injectTime = GetInjectTime(entity, user, target);
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, injectTime, new HyposprayDoAfterEvent(), entity.Owner, target: target, used: entity.Owner)
        {
            BreakOnMove = true,
            BreakOnWeightlessMove = false,
            BreakOnDamage = true,
            NeedHand = component.NeedHand,
            BreakOnHandChange = component.BreakOnHandChange,
            MovementThreshold = component.MovementThreshold,
        });
    }

    private void OnUseInHand(Entity<HyposprayComponent> entity, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = TryDoInject(entity, args.User, args.User);
    }

    public void OnAfterInteract(Entity<HyposprayComponent> entity, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target == null)
            return;

        args.Handled = TryUseHypospray(entity, args.Target.Value, args.User);
    }

    public void OnAttack(Entity<HyposprayComponent> entity, ref MeleeHitEvent args)
    {
        if (!args.HitEntities.Any())
            return;

        if (entity.Comp.PreventCombatInjection) // Frontier
            return; // Frontier

        TryDoInject(entity, args.HitEntities.First(), args.User);
    }

    public bool TryDoInject(Entity<HyposprayComponent> entity, EntityUid target, EntityUid user)
    {
        var (uid, component) = entity;

        if (!EligibleEntity(target, EntityManager, component))
            return false;

        if (TryComp(uid, out UseDelayComponent? delayComp))
        {
            if (_useDelay.IsDelayed((uid, delayComp)))
                return false;
        }

        // Frontier: Block hypospray injections
        if (TryComp<BlockInjectionComponent>(target, out var blockInjection) && blockInjection.BlockHypospray)
        {
            _popup.PopupEntity(Loc.GetString("injector-component-deny-user"), target, user);
            return false;
        }
        // End Frontier

        string? msgFormat = null;

        // Self event
        var selfEvent = new SelfBeforeHyposprayInjectsEvent(user, entity.Owner, target);
        RaiseLocalEvent(user, selfEvent);

        if (selfEvent.Cancelled)
        {
            _popup.PopupEntity(Loc.GetString(selfEvent.InjectMessageOverride ?? "hypospray-cant-inject", ("owner", Identity.Entity(target, EntityManager))), target, user);
            return false;
        }

        target = selfEvent.TargetGettingInjected;

        if (!EligibleEntity(target, EntityManager, component))
            return false;

        if (!component.PierceArmor && _inventory.TryGetSlotEntity(target, "outerClothing", out var suit) && _tag.HasTag(suit.Value, HardsuitTag)) // Hardlight start
        {
            _popup.PopupEntity(Loc.GetString(component.BlockedByHardsuitPopupMessage, ("weapon", entity.Owner)), entity.Owner, user, PopupType.SmallCaution);

            return false;
        } // Hardlight end

        // Target event
        var targetEvent = new TargetBeforeHyposprayInjectsEvent(user, entity.Owner, target);
        RaiseLocalEvent(target, targetEvent);

        if (targetEvent.Cancelled)
        {
            _popup.PopupEntity(Loc.GetString(targetEvent.InjectMessageOverride ?? "hypospray-cant-inject", ("owner", Identity.Entity(target, EntityManager))), target, user);
            return false;
        }

        target = targetEvent.TargetGettingInjected;

        if (!EligibleEntity(target, EntityManager, component))
            return false;

        // The target event gets priority for the overriden message.
        if (targetEvent.InjectMessageOverride != null)
            msgFormat = targetEvent.InjectMessageOverride;
        else if (selfEvent.InjectMessageOverride != null)
            msgFormat = selfEvent.InjectMessageOverride;
        else if (target == user)
            msgFormat = "hypospray-component-inject-self-message";

        // Frontier - Upstream: #30704 - MIT
        // if (!_solutionContainers.TryGetSolution(uid, component.SolutionName, out var hypoSpraySoln, out var hypoSpraySolution) || hypoSpraySolution.Volume == 0)
        // {
        //     _popup.PopupEntity(Loc.GetString("hypospray-component-empty-message"), target, user);
        //     return true;
        // }

        // if (!_solutionContainers.TryGetInjectableSolution(target, out var targetSoln, out var targetSolution))
        // {
        //     _popup.PopupEntity(Loc.GetString("hypospray-cant-inject", ("target", Identity.Entity(target, EntityManager))), target, user);
        //     return false;
        // }

        if (!InjectionFailureCheck(entity, target, user, out var hypoSpraySoln, out var targetSoln, out var targetSolution, out var returnValue)
            || hypoSpraySoln == null
            || targetSoln == null
            || targetSolution == null)
            return returnValue;
        // End Frontier

        _popup.PopupEntity(Loc.GetString(msgFormat ?? "hypospray-component-inject-other-message", ("other", target)), target, user);

        if (target != user)
        {
            _popup.PopupEntity(Loc.GetString("hypospray-component-feel-prick-message"), target, target);
            // TODO: This should just be using melee attacks...
            // meleeSys.SendLunge(angle, user);
        }

        // Get transfer amount. May be smaller than component.TransferAmount if not enough room
        // If InjectMaxCapacity is enabled, inject as much as possible from current contents.
        var plannedTransfer = component.InjectMaxCapacity ? hypoSpraySoln.Value.Comp.Solution.Volume : component.TransferAmount;
        var realTransferAmount = FixedPoint2.Min(plannedTransfer, targetSolution.AvailableVolume);

        if (realTransferAmount <= 0)
        {
            _popup.PopupEntity(Loc.GetString("hypospray-component-transfer-already-full-message", ("owner", target)), target, user);
            return true;
        }

        _audio.PlayPvs(component.InjectSound, user);

        // Medipens and such use this system and don't have a delay, requiring extra checks
        // BeginDelay function returns if item is already on delay
        if (delayComp != null)
            _useDelay.TryResetDelay((uid, delayComp));

        // Move units from attackSolution to targetSolution
        var removedSolution = _solutionContainers.SplitSolution(hypoSpraySoln.Value, realTransferAmount);

        if (!targetSolution.CanAddSolution(removedSolution))
            return true;
        _reactiveSystem.DoEntityReaction(target, removedSolution, ReactionMethod.Injection);
        _solutionContainers.TryAddSolution(targetSoln.Value, removedSolution);

        var ev = new TransferDnaEvent { Donor = target, Recipient = uid };
        RaiseLocalEvent(target, ref ev);

        // same LogType as syringes...
        _adminLogger.Add(LogType.ForceFeed, $"{EntityManager.ToPrettyString(user):user} injected {EntityManager.ToPrettyString(target):target} with a solution {SharedSolutionContainerSystem.ToPrettyString(removedSolution):removedSolution} using a {EntityManager.ToPrettyString(uid):using}");

        return true;
    }

    /// <summary>
    /// Calculates the do-after time for non-instant hyposprays and emits attempt popups.
    /// </summary>
    private TimeSpan GetInjectTime(Entity<HyposprayComponent> hypospray, EntityUid user, EntityUid target)
    {
        _popup.PopupEntity(Loc.GetString("hypospray-component-injecting-user"), target, user);
        var comp = hypospray.Comp;

        if (!_solutionContainers.TryGetSolution(hypospray.Owner, comp.SolutionName, out _, out var solution))
            return TimeSpan.Zero;

        var actualDelay = comp.Delay;
        var amountToInject = comp.InjectMaxCapacity
            ? solution.Volume
            : FixedPoint2.Min(comp.TransferAmount, solution.Volume);

        // First 5u does not add extra delay.
        actualDelay += comp.DelayPerVolume * FixedPoint2.Max(0, amountToInject - 5).Double();
        actualDelay = MathHelper.Max(actualDelay, TimeSpan.FromSeconds(1));

        if (user != target)
        {
            var userName = Identity.Entity(user, EntityManager);
            _popup.PopupEntity(Loc.GetString("hypospray-component-injecting-target", ("user", userName)), user, target);

            if (_mobState.IsIncapacitated(target))
                actualDelay /= 2.5f;
            else if (_combatMode.IsInCombatMode(target))
                actualDelay += TimeSpan.FromSeconds(1);

            _adminLogger.Add(LogType.ForceFeed,
                $"{ToPrettyString(user):user} is attempting to inject {ToPrettyString(target):target} with a solution {SharedSolutionContainerSystem.ToPrettyString(solution):solution}");
        }
        else
        {
            actualDelay /= 2;
            _adminLogger.Add(LogType.Ingestion,
                $"{ToPrettyString(user):user} is attempting to inject themselves with a solution {SharedSolutionContainerSystem.ToPrettyString(solution):solution}.");
        }

        return actualDelay;
    }

    private bool TryDraw(Entity<HyposprayComponent> entity, Entity<BloodstreamComponent?> target, Entity<SolutionComponent> targetSolution, EntityUid user)
    {
        if (!_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.SolutionName, out var soln,
                out var solution) || solution.AvailableVolume == 0)
        {
            return false;
        }

        // Get transfer amount. May be smaller than _transferAmount if not enough room, also make sure there's room in the injector
        var realTransferAmount = FixedPoint2.Min(entity.Comp.TransferAmount, targetSolution.Comp.Solution.Volume,
            solution.AvailableVolume);

        if (realTransferAmount <= 0)
        {
            _popup.PopupEntity(
                Loc.GetString("injector-component-target-is-empty-message",
                    ("target", Identity.Entity(target, EntityManager))),
                entity.Owner, user);
            return false;
        }

        var removedSolution = _solutionContainers.Draw(target.Owner, targetSolution, realTransferAmount);

        if (!_solutionContainers.TryAddSolution(soln.Value, removedSolution))
        {
            return false;
        }

        _popup.PopupEntity(Loc.GetString("injector-component-draw-success-message",
            ("amount", removedSolution.Volume),
            ("target", Identity.Entity(target, EntityManager))), entity.Owner, user);
        return true;
    }

    private bool EligibleEntity(EntityUid entity, IEntityManager entMan, HyposprayComponent component)
    {
        // TODO: Does checking for BodyComponent make sense as a "can be hypospray'd" tag?
        // In SS13 the hypospray ONLY works on mobs, NOT beakers or anything else.
        // But this is 14, we dont do what SS13 does just because SS13 does it.
        return component.OnlyAffectsMobs
            ? entMan.HasComponent<SolutionContainerManagerComponent>(entity) &&
              entMan.HasComponent<MobStateComponent>(entity)
            : entMan.HasComponent<SolutionContainerManagerComponent>(entity);
    }

    // Frontier: Upstream: #30704 - MIT
    private bool InjectionFailureCheck(Entity<HyposprayComponent> entity, EntityUid target, EntityUid user, out Entity<SolutionComponent>? hypoSpraySoln, out Entity<SolutionComponent>? targetSoln, out Solution? targetSolution, out bool returnValue)
    {
        hypoSpraySoln = null;
        targetSoln = null;
        targetSolution = null;
        returnValue = false;

        if (!_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.SolutionName, out hypoSpraySoln, out var hypoSpraySolution) || hypoSpraySolution.Volume == 0)
        {
            _popup.PopupEntity(Loc.GetString("hypospray-component-empty-message"), target, user);
            returnValue = true;
            return false;
        }

        if (!_solutionContainers.TryGetInjectableSolution(target, out targetSoln, out targetSolution))
        {
            _popup.PopupEntity(Loc.GetString("hypospray-cant-inject", ("target", Identity.Entity(target, EntityManager))), target, user);
            returnValue = false;
            return false;
        }

        return true;
    }
    // End Frontier
}
