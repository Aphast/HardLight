using Content.Server.Salvage.Expeditions;
using Content.Server.Worldgen.Components;

namespace Content.Server.Salvage;

public sealed partial class SalvageSystem
{
    private const float ExpeditionResolvePadding = 256f;

    public bool IsOnExpedition(EntityUid entity, TransformComponent? xform = null)
    {
        return TryGetExpeditionForEntity(entity, out _, out _, xform);
    }

    private bool TryGetExpeditionForEntity(EntityUid entity, out EntityUid expeditionUid, out SalvageExpeditionComponent expedition, TransformComponent? xform = null)
    {
        expeditionUid = EntityUid.Invalid;
        expedition = default!;

        if (TryComp<SalvageExpeditionComponent>(entity, out var expeditionComp) && expeditionComp != null)
        {
            expeditionUid = entity;
            expedition = expeditionComp;
            return true;
        }

        if (!Resolve(entity, ref xform, false))
            return false;

        if (xform.GridUid is { } gridUid && TryComp<SalvageExpeditionComponent>(gridUid, out expeditionComp) && expeditionComp != null)
        {
            expeditionUid = gridUid;
            expedition = expeditionComp;
            return true;
        }

        if (xform.MapUid is not { } mapUid)
            return false;

        if (TryComp<SalvageExpeditionComponent>(mapUid, out expeditionComp) && expeditionComp != null)
        {
            expeditionUid = mapUid;
            expedition = expeditionComp;
            return true;
        }

        var worldPos = _transform.GetWorldPosition(xform, _xformQuery);
        var maxDistanceSquared = float.MaxValue;
        var query = EntityQueryEnumerator<SalvageExpeditionComponent, SectorExpeditionSiteComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var nearbyExpedition, out var site, out var expeditionXform))
        {
            if (expeditionXform.MapUid != mapUid)
                continue;

            var resolveRadius = site.Radius + ExpeditionResolvePadding;
            var distanceSquared = (site.Center - worldPos).LengthSquared();
            if (distanceSquared > resolveRadius * resolveRadius || distanceSquared >= maxDistanceSquared)
                continue;

            maxDistanceSquared = distanceSquared;
            expeditionUid = uid;
            expedition = nearbyExpedition;
        }

        return expeditionUid != EntityUid.Invalid;
    }

    private bool IsEntityOnExpedition(EntityUid entity, EntityUid expeditionUid, TransformComponent? xform = null)
    {
        if (!Resolve(entity, ref xform, false))
            return false;

        if (xform.GridUid == expeditionUid)
            return true;

        if (!TryComp(expeditionUid, out SectorExpeditionSiteComponent? site))
            return xform.MapUid == expeditionUid;

        if (xform.MapUid != site.SectorMap)
            return false;

        var worldPos = _transform.GetWorldPosition(xform, _xformQuery);
        var resolveRadius = site.Radius + ExpeditionResolvePadding;
        return (site.Center - worldPos).LengthSquared() <= resolveRadius * resolveRadius;
    }
}