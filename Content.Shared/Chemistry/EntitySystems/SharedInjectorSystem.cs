using System.Linq;
using Content.Shared.Administration.Logs;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Prototypes;
using Content.Shared.CombatMode;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;
using Robust.Shared.Player;

namespace Content.Shared.Chemistry.EntitySystems;

public abstract class SharedInjectorSystem : EntitySystem
{
    /// <summary>
    ///     Default transfer amounts for the set-transfer verb.
    /// </summary>
    public static readonly FixedPoint2[] TransferAmounts = { 1, 5, 10, 15 };

    [Dependency] protected readonly SharedPopupSystem Popup = default!;
    [Dependency] protected readonly SharedSolutionContainerSystem SolutionContainers = default!;
    [Dependency] protected readonly MobStateSystem MobState = default!;
    [Dependency] protected readonly SharedCombatModeSystem Combat = default!;
    [Dependency] protected readonly SharedDoAfterSystem DoAfter = default!;
    [Dependency] protected readonly ISharedAdminLogManager AdminLogger = default!;
    [Dependency] protected readonly IPrototypeManager Prototypes = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<InjectorComponent, GetVerbsEvent<AlternativeVerb>>(AddSetTransferVerbs);
        SubscribeLocalEvent<InjectorComponent, ComponentStartup>(OnInjectorStartup);
        SubscribeLocalEvent<InjectorComponent, UseInHandEvent>(OnInjectorUse);
    }

    private void AddSetTransferVerbs(Entity<InjectorComponent> entity, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands == null)
            return;

        var user = args.User;
        var component = entity.Comp;

        // If an active mode is set, use its transfer amounts exclusively — even if empty.
        // An empty list means "no selectable amounts" (jet injectors always inject the full reservoir).
        var transferSet = TransferAmounts.AsEnumerable();
        if (TryGetActiveMode(entity, out var mode))
            transferSet = mode.TransferAmounts;

        var amounts = transferSet.Distinct().Order().ToArray();
        if (amounts.Length == 0)
            return;

        var min = amounts.First();
        var max = amounts.Last();
        var cur = component.CurrentTransferAmount ?? component.TransferAmount;
        var toggleAmount = cur == max ? min : max;

        var priority = 0;
        AlternativeVerb toggleVerb = new()
        {
            Text = Loc.GetString("comp-solution-transfer-verb-toggle", ("amount", toggleAmount)),
            Category = VerbCategory.SetTransferAmount,
            Act = () =>
            {
                component.TransferAmount = toggleAmount;
                component.CurrentTransferAmount = toggleAmount;
                Popup.PopupClient(Loc.GetString("comp-solution-transfer-set-amount", ("amount", toggleAmount)), user, user);
                Dirty(entity);
            },

            Priority = priority
        };
        args.Verbs.Add(toggleVerb);

        priority -= 1;

        // Add specific transfer verbs according to the active mode / injector config.
        foreach (var amount in amounts)
        {
            AlternativeVerb verb = new()
            {
                Text = Loc.GetString("comp-solution-transfer-verb-amount", ("amount", amount)),
                Category = VerbCategory.SetTransferAmount,
                Act = () =>
                {
                    component.TransferAmount = amount;
                    component.CurrentTransferAmount = amount;
                    Popup.PopupClient(Loc.GetString("comp-solution-transfer-set-amount", ("amount", amount)), user, user);
                    Dirty(entity);
                },

                // we want to sort by size, not alphabetically by the verb text.
                Priority = priority
            };

            priority -= 1;

            args.Verbs.Add(verb);
        }

        if (component.AllowedModes is { Count: > 1 })
        {
            AlternativeVerb toggleModeVerb = new()
            {
                Text = Loc.GetString("injector-toggle-verb-text"),
                Category = VerbCategory.SetTransferAmount,
                Act = () =>
                {
                    Toggle(entity, user);
                },
                Priority = priority - 1,
            };
            args.Verbs.Add(toggleModeVerb);
        }
    }

    private void OnInjectorStartup(Entity<InjectorComponent> entity, ref ComponentStartup args)
    {
        SyncLegacyFieldsFromMode(entity);
        Dirty(entity);
    }

    private void OnInjectorUse(Entity<InjectorComponent> entity, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;

        Toggle(entity, args.User);
        args.Handled = true;
    }

    /// <summary>
    /// Toggle between draw/inject state if applicable
    /// </summary>
    private void Toggle(Entity<InjectorComponent> injector, EntityUid user)
    {
        if (injector.Comp.AllowedModes is { Count: > 1 } allowed)
        {
            // If no active mode is set yet, default to the first allowed mode so the toggle is usable.
            var current = injector.Comp.ActiveModeProtoId ?? allowed[0];
            var index = allowed.FindIndex(p => p == current);
            if (index < 0)
                index = 0;

            var next = allowed[(index + 1) % allowed.Count];
            injector.Comp.ActiveModeProtoId = next;
            SyncLegacyFieldsFromMode(injector);
            var key = injector.Comp.ToggleState == InjectorToggleMode.Draw
                ? "injector-component-drawing-text"
                : "injector-component-injecting-text";
            Popup.PopupClient(Loc.GetString(key), injector, user);
            Dirty(injector);
            return;
        }

        // Mode-locked injectors (single allowed mode with an active mode) shouldn't fall through to
        // legacy toggle, which would flip ToggleState pointlessly and emit misleading popups.
        if (injector.Comp.AllowedModes is { Count: 1 } && injector.Comp.ActiveModeProtoId != null)
            return;

        if (injector.Comp.InjectOnly)
            return;

        if (!SolutionContainers.TryGetSolution(injector.Owner, injector.Comp.SolutionName, out var solEnt, out var solution))
            return;

        string msg;

        switch (injector.Comp.ToggleState)
        {
            case InjectorToggleMode.Inject:
                if (solution.AvailableVolume > 0) // If solution has empty space to fill up, allow toggling to draw
                {
                    SetMode(injector, InjectorToggleMode.Draw);
                    msg = "injector-component-drawing-text";
                }
                else
                {
                    msg = "injector-component-cannot-toggle-draw-message";
                }
                break;
            case InjectorToggleMode.Draw:
                if (solution.Volume > 0) // If solution has anything in it, allow toggling to inject
                {
                    SetMode(injector, InjectorToggleMode.Inject);
                    msg = "injector-component-injecting-text";
                }
                else
                {
                    msg = "injector-component-cannot-toggle-inject-message";
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        Popup.PopupClient(Loc.GetString(msg), injector, user);
    }

    public void SetMode(Entity<InjectorComponent> injector, InjectorToggleMode mode)
    {
        injector.Comp.ToggleState = mode;
        Dirty(injector);
    }

    protected bool TryGetActiveMode(Entity<InjectorComponent> injector, out InjectorModePrototype mode)
    {
        mode = default!;
        var id = injector.Comp.ActiveModeProtoId;
        if (id == null || !Prototypes.TryIndex(id.Value, out var indexedMode))
            return false;

        mode = indexedMode;
        return true;
    }

    protected void SyncLegacyFieldsFromMode(Entity<InjectorComponent> injector)
    {
        if (!TryGetActiveMode(injector, out var mode))
            return;

        if (mode.TransferAmounts.Count > 0)
        {
            var values = mode.TransferAmounts.Order().ToArray();
            injector.Comp.MinimumTransferAmount = values.First();
            injector.Comp.MaximumTransferAmount = values.Last();

            var selected = injector.Comp.CurrentTransferAmount;
            if (selected == null)
                selected = values.Last();

            injector.Comp.CurrentTransferAmount = selected;
            injector.Comp.TransferAmount = selected.Value;
        }
        else
        {
            // Modes with no transfer amounts (e.g. jet injectors) should always use
            // runtime fallback volume in GetTransferAmount, not a stale persisted value.
            injector.Comp.CurrentTransferAmount = null;
        }

        if (mode.Behavior.HasFlag(InjectorBehavior.Draw))
            injector.Comp.ToggleState = InjectorToggleMode.Draw;
        else
            injector.Comp.ToggleState = InjectorToggleMode.Inject;

        injector.Comp.Delay = mode.MobTime;
        injector.Comp.DelayPerVolume = mode.DelayPerVolume;

        if (injector.Comp.AllowedModes is { Count: > 0 })
        {
            var hasDraw = false;
            foreach (var allowed in injector.Comp.AllowedModes)
            {
                if (Prototypes.TryIndex(allowed, out var allowedMode) && allowedMode.Behavior.HasAnyFlag(InjectorBehavior.Draw | InjectorBehavior.Dynamic))
                {
                    hasDraw = true;
                    break;
                }
            }

            injector.Comp.InjectOnly = !hasDraw;
        }
    }
}
