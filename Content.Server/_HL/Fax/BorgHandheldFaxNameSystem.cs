using Content.Shared.Fax.Components;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Silicons.Borgs.Components;

namespace Content.Server._HL.Fax;

/// <summary>
/// Syncs a borg-provided handheld fax's network-visible fax name to the borg currently holding it.
/// </summary>
public sealed class BorgHandheldFaxNameSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BorgHandheldFaxNameComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<BorgHandheldFaxNameComponent, GotEquippedHandEvent>(OnEquipped);
        SubscribeLocalEvent<BorgHandheldFaxNameComponent, GotUnequippedHandEvent>(OnUnequipped);
        SubscribeLocalEvent<BorgChassisComponent, EntityRenamedEvent>(OnBorgRenamed);
    }

    private void OnMapInit(Entity<BorgHandheldFaxNameComponent> ent, ref MapInitEvent args)
    {
        if (!TryComp<FaxMachineComponent>(ent, out var fax))
            return;

        ent.Comp.DefaultName ??= fax.FaxName;
    }

    private void OnEquipped(Entity<BorgHandheldFaxNameComponent> ent, ref GotEquippedHandEvent args)
    {
        SetFaxNameFromHolder(ent.Owner, args.User);
    }

    private void OnUnequipped(Entity<BorgHandheldFaxNameComponent> ent, ref GotUnequippedHandEvent args)
    {
        RestoreDefaultFaxName(ent);
    }

    private void OnBorgRenamed(Entity<BorgChassisComponent> ent, ref EntityRenamedEvent args)
    {
        if (!TryComp<HandsComponent>(ent, out var hands))
            return;

        foreach (var hand in hands.Hands.Values)
        {
            if (hand.HeldEntity is not { } held ||
                !HasComp<BorgHandheldFaxNameComponent>(held))
            {
                continue;
            }

            SetFaxNameFromHolder(held, ent.Owner);
        }
    }

    private void SetFaxNameFromHolder(EntityUid faxUid, EntityUid holderUid)
    {
        if (!TryComp<FaxMachineComponent>(faxUid, out var fax))
            return;

        var holderName = Name(holderUid);
        if (fax.FaxName == holderName)
            return;

        fax.FaxName = holderName;
    }

    private void RestoreDefaultFaxName(Entity<BorgHandheldFaxNameComponent> ent)
    {
        if (!TryComp<FaxMachineComponent>(ent, out var fax))
            return;

        var defaultName = ent.Comp.DefaultName ?? "Unknown";
        if (fax.FaxName == defaultName)
            return;

        fax.FaxName = defaultName;
    }
}
