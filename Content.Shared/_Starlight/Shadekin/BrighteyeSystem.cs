using Content.Shared._Starlight.NullSpace;
using Content.Shared.Interaction.Events;

namespace Content.Shared._Starlight.Shadekin;

public sealed class BrighteyeSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BrighteyeComponent, InteractionAttemptEvent>(OnInteractionAttempt);
        SubscribeLocalEvent<BrighteyeComponent, AttackAttemptEvent>(OnAttackAttempt);
    }

    private void OnAttackAttempt(EntityUid uid, BrighteyeComponent component, AttackAttemptEvent args)
    {
        if (HasComp<NullSpaceComponent>(uid))
            return;

        if (HasComp<NullSpaceComponent>(args.Target))
            args.Cancel();
    }

    private void OnInteractionAttempt(EntityUid uid, BrighteyeComponent component, ref InteractionAttemptEvent args)
    {
        if (HasComp<NullSpaceComponent>(uid))
            return;

        if (HasComp<NullSpaceComponent>(args.Target))
            args.Cancelled = true;
    }
}
