using Content.Server.Body.Systems;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Humanoid;
using Robust.Server.GameObjects;

namespace Content.Server._DV.Traits.Assorted;

public sealed class AmputeeSystem : EntitySystem
{
    [Dependency] private readonly BodySystem _body = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly BloodstreamSystem _bloodstream = default!;
    [Dependency] private readonly SharedHumanoidAppearanceSystem _humanoid = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AmputeeComponent, MapInitEvent>(OnMapInit);
    }
    // Logic here taken from Den at https://github.com/TheDenSS14/TheDen/blob/d6f85a10fccf0f282981438e55b808d7ece73ad9/Content.Server/Traits/TraitSystem.Functions.cs
    private void OnMapInit(Entity<AmputeeComponent> ent, ref MapInitEvent args)
    {
        if (!TryComp(ent, out BodyComponent? body) || !TryComp(ent, out TransformComponent? xform))
            return;

        var root = _body.GetRootPartOrNull(ent, body);
        if (root is null)
            return;

        // VRS: capture humanoid up-front so we can explicitly hide the base sprite layer(s) for the
        // missing limb(s). The Shitmed body system's RemoveAppearance only hides marking sublayers
        // when a part is detached; the base humanoid layer (e.g. LLeg / RLeg / LArm / RArm) and its
        // dependent sublayers (foot/hand) keep rendering otherwise, leaving a "ghost" limb visible
        // even though the body part entity is gone.
        TryComp<HumanoidAppearanceComponent>(ent, out var humanoid);

        var parts = _body.GetBodyChildrenOfType(ent, ent.Comp.RemoveBodyPart, body);
        foreach (var part in parts)
        {
            var partComp = part.Component;
            if (!ent.Comp.IgnoreSymmetry && partComp.Symmetry != ent.Comp.PartSymmetry)
                continue;

            // Resolve which humanoid layers this part owns BEFORE we orphan / delete it.
            var baseLayer = partComp.ToHumanoidLayers();

            foreach (var child in _body.GetBodyPartChildren(part.Id, part.Component))
            {
                QueueDel(child.Id);
            }

            // AttachToGridOrMap pulls the part out of its body container, which triggers
            // SharedBodySystem.RemovePart -> BodyPartRemovedEvent -> Shitmed RemoveAppearance.
            // That handles marking sublayers; we still hide the base + dependent layers below.
            _transform.AttachToGridOrMap(part.Id);
            QueueDel(part.Id);

            // Permanently hide the base humanoid layer and its sublayers (e.g. LLeg + LFoot).
            // Pass source=null so the layer is added to PermanentlyHidden rather than tied to a
            // clothing slot, matching how a truly missing limb should behave.
            if (humanoid != null && baseLayer is { } layer)
            {
                foreach (var sublayer in HumanoidVisualLayersExtension.Sublayers(layer))
                    _humanoid.SetLayerVisibility((ent.Owner, humanoid), sublayer, false);

                // Sublayers() includes the base layer for arm/leg cases, but defensively hide it
                // again in case ToHumanoidLayers ever resolves to a layer with no Sublayers entry.
                _humanoid.SetLayerVisibility((ent.Owner, humanoid), layer, false);
            }

            // apparently chopping off limbs makes people bleed a lot. Who would have guessed?
            _bloodstream.TryModifyBleedAmount(ent.Owner, -10f);

            // goes unused for the purposes of the arm amputee traits, but might as well keep it in
            if (ent.Comp.ProtoId is null || ent.Comp.SlotId == null)
                continue;

            var newLimb = SpawnAtPosition(ent.Comp.ProtoId, xform.Coordinates);
            if (TryComp<BodyPartComponent>(newLimb, out var limbComp) && limbComp.Symmetry == ent.Comp.PartSymmetry)
                _body.AttachPart(root.Value.Entity, ent.Comp.SlotId, newLimb, root.Value.BodyPart, limbComp);
        }
    }
}
