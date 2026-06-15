/* using Content.Shared.Atmos.Components;
using Content.Shared.SprayPainter;
using Content.Shared.SprayPainter.Components;
using Content.Shared.SprayPainter.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.SprayPainter;

/// <summary>
/// Handles persistence of spray-paint styles across ship saves.
/// When an entity with a <see cref="PaintableComponent"/> is painted, this system
/// stores the applied style in <see cref="SprayPaintOnMapInitComponent"/>. On the
/// next MapInit (e.g. ship reload after save), the stored style is re-applied so
/// the entity retains its painted appearance.
/// Ported from Triad Sector #20 (76477fc).
/// </summary>
public sealed class SprayPaintPersistenceSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SprayPaintOnMapInitComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<PaintableComponent, EntityPaintedEvent>(OnPainted);
    }

    private void OnMapInit(Entity<SprayPaintOnMapInitComponent> ent, ref MapInitEvent args)
    {
        SprayPaint(ent, ent.Comp.Style);
    }

    private void OnPainted(Entity<PaintableComponent> ent, ref EntityPaintedEvent args)
    {
        // Pipes are handled separately in AtmosPipeColorSystem.
        if (HasComp<PipeAppearanceComponent>(ent))
            return;

        var paintOnMapInit = EnsureComp<SprayPaintOnMapInitComponent>(ent);
        paintOnMapInit.Style = args.Prototype.Id;
    }

    /// <summary>
    /// Re-applies a previously stored paint style to the target entity by
    /// locating the corresponding <see cref="PaintableGroupPrototype"/> and
    /// raising <see cref="EntityPaintedEvent"/>.
    /// </summary>
    public void SprayPaint(EntityUid target, EntProtoId style)
    {
        if (!TryComp<PaintableComponent>(target, out _))
            return;

        // Find which group contains this style prototype.
        ProtoId<PaintableGroupPrototype>? groupId = null;
        foreach (var group in _proto.EnumeratePrototypes<PaintableGroupPrototype>())
        {
            if (group.Styles.ContainsValue(style))
            {
                groupId = group.ID;
                break;
            }
        }

        if (groupId == null)
            return;

        // EntityPaintedEvent.Tool is not nullable; use the target entity itself as a stand-in.
        var ev = new EntityPaintedEvent(
            User: null,
            Tool: target,
            Prototype: style.Id,
            Group: groupId.Value);
        RaiseLocalEvent(target, ref ev);
    }
}
 */