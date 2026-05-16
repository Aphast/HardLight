using System.Linq;
using Content.Server.Actions;
using Content.Server.Humanoid;
using Content.Shared._CS.Humanoid;
using Content.Shared._Shitmed.Humanoid.Events; // Hardlight
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Mobs;
using Content.Shared.Toggleable;
using Content.Shared.Wagging;
using Robust.Shared.Prototypes;

namespace Content.Server.Wagging;

/// <summary>
/// Adds an action to toggle wagging animation for tails markings that supporting this
/// </summary>
public sealed class WaggingSystem : EntitySystem
{
    [Dependency] private readonly ActionsSystem _actions = default!;
    [Dependency] private readonly HumanoidAppearanceSystem _humanoidAppearance = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly CoyoteMarkingSystem _coyoteMarking = default!; // Coyote, obviously

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<WaggingComponent, ComponentInit>(OnWaggingMapInit); // Coyote: Move to Component Init
        SubscribeLocalEvent<WaggingComponent, ProfileLoadFinishedEvent>(OnProfileLoadFinished); // Hardlight
        SubscribeLocalEvent<WaggingComponent, ComponentShutdown>(OnWaggingShutdown);
        SubscribeLocalEvent<WaggingComponent, ToggleActionEvent>(OnWaggingToggle);
        SubscribeLocalEvent<WaggingComponent, MobStateChangedEvent>(OnMobStateChanged);
    }

    private void OnWaggingMapInit(EntityUid uid, WaggingComponent component, ComponentInit args) // Coyote: Move to Component Init
    {
        // Hardlight start
        EnsureWaggingAction(uid, component);
    }

    private void OnProfileLoadFinished(EntityUid uid, WaggingComponent component, ref ProfileLoadFinishedEvent args)
    {
        EnsureWaggingAction(uid, component);
    }

    private void EnsureWaggingAction(EntityUid uid, WaggingComponent component)
    {
        // Hardlight end
        if (!TryComp<HumanoidAppearanceComponent>(uid, out var humanoid))
            return;

        if (!component.Wagging)
            NormalizeTailMarkingsToStatic(uid, humanoid);

        if (!humanoid.MarkingSet.Markings.TryGetValue(MarkingCategories.Tail, out var markings))
            return;

        if (markings.Any(m =>
                _coyoteMarking.TryGetWaggingId(m.MarkingId, out _) ||
                _coyoteMarking.TryGetStaticId(m.MarkingId, out _)))
        {
            _actions.AddAction(uid, ref component.ActionEntity, component.Action, uid);
        }
    }

    private void NormalizeTailMarkingsToStatic(EntityUid uid, HumanoidAppearanceComponent humanoid)
    {
        if (!humanoid.MarkingSet.Markings.TryGetValue(MarkingCategories.Tail, out var markings))
            return;

        for (var idx = 0; idx < markings.Count; idx++)
        {
            if (_coyoteMarking.TryGetStaticId(markings[idx].MarkingId, out var staticId))
                _humanoidAppearance.SetMarkingId(uid, MarkingCategories.Tail, idx, staticId, humanoid: humanoid);
        }
    }

    private void OnWaggingShutdown(EntityUid uid, WaggingComponent component, ComponentShutdown args)
    {
        _actions.RemoveAction(uid, component.ActionEntity);
    }

    private void OnWaggingToggle(EntityUid uid, WaggingComponent component, ref ToggleActionEvent args)
    {
        if (args.Handled)
            return;

        TryToggleWagging(uid, wagging: component);
    }

    private void OnMobStateChanged(EntityUid uid, WaggingComponent component, MobStateChangedEvent args)
    {
        if (component.Wagging)
            TryToggleWagging(uid, wagging: component);
    }

    public bool TryToggleWagging(EntityUid uid, WaggingComponent? wagging = null, HumanoidAppearanceComponent? humanoid = null)
    {
        if (!Resolve(uid, ref wagging, ref humanoid))
            return false;

        if (!humanoid.MarkingSet.Markings.TryGetValue(MarkingCategories.Tail, out var markings))
            return false;

        if (markings.Count == 0)
            return false;

        var hasSupportedMarking = markings.Any(m =>
            _coyoteMarking.TryGetWaggingId(m.MarkingId, out _) ||
            _coyoteMarking.TryGetStaticId(m.MarkingId, out _));

        if (!hasSupportedMarking)
            return false;

        var isCurrentlyWagging = markings.Any(m => _coyoteMarking.TryGetStaticId(m.MarkingId, out _));
        wagging.Wagging = !isCurrentlyWagging;

        for (var idx = 0; idx < markings.Count; idx++) // Coyote: Improved wagging system
        {
            var target = markings[idx].MarkingId;
            if (wagging.Wagging)
            {
                if (_coyoteMarking.TryGetWaggingId(markings[idx].MarkingId, out var waggingId))
                    target = waggingId;
            }
            else
            {
                if (_coyoteMarking.TryGetStaticId(markings[idx].MarkingId, out var staticId))
                    target = staticId;
            }

            if (target != markings[idx].MarkingId)
                _humanoidAppearance.SetMarkingId(uid, MarkingCategories.Tail, idx, target, humanoid: humanoid);
        } // Coyote end

        return true;
    }
}
