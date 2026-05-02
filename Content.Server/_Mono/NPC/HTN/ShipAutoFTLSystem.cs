using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._Mono.NPC.HTN;

/// <summary>
/// Monitors <see cref="ShipAutoFTLComponent"/> on AI ships and automatically triggers FTL
/// if the ship gets too close to a player station, preventing them from camping docks.
/// </summary>
public sealed class ShipAutoFTLSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private TimeSpan _nextCheck = TimeSpan.Zero;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextCheck)
            return;

        _nextCheck = now + CheckInterval;

        var query = EntityQueryEnumerator<ShipAutoFTLComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var autoFTL, out var xform))
        {
            if (xform.MapID == MapId.Nullspace)
                continue;

            var shipPos = _xform.GetMapCoordinates(xform);

            // Find nearest player station
            var nearestStation = GetNearestStation(shipPos);
            if (nearestStation == null)
                continue;

            var stationPos = _xform.GetMapCoordinates((EntityUid)nearestStation);
            var distance = (shipPos.Position - stationPos.Position).Length();

            // If we're closer than the FTL trigger distance, FTL away
            if (distance <= autoFTL.FTLTriggerDistance)
            {
                TriggerAutoFTL(uid, stationPos, autoFTL);
            }
        }
    }

    private void TriggerAutoFTL(EntityUid shipUid, MapCoordinates stationPos, ShipAutoFTLComponent autoFTL)
    {
        // Compute FTL destination: 10km away from station, on the line from station through ship
        var xform = Transform(shipUid);
        var shipMapPos = _xform.GetMapCoordinates(xform);

        var toShip = (shipMapPos.Position - stationPos.Position);
        if (toShip == Vector2.Zero)
            toShip = new Vector2(1f, 0f);
        toShip = toShip.Normalized();
        var ftlDestPos = stationPos.Position + toShip * autoFTL.FTLTargetDistance;

        // Get the shuttle grid that this NPC entity is sitting on
        var gridUid = xform.GridUid;
        if (gridUid == null || !TryComp<ShuttleComponent>(gridUid, out var shuttleComp))
            return;

        var mapUid = _mapSystem.GetMap(stationPos.MapId);
        var dest = new EntityCoordinates(mapUid, ftlDestPos);
        _shuttle.FTLToCoordinates(gridUid.Value, shuttleComp, dest, Angle.Zero);
    }

    /// <summary>
    /// Find the nearest player station in the current map.
    /// </summary>
    private EntityUid? GetNearestStation(MapCoordinates pos)
    {
        var query = EntityQueryEnumerator<StationDataComponent, TransformComponent>();
        EntityUid? nearest = null;
        var nearestDistSq = float.PositiveInfinity;

        while (query.MoveNext(out var uid, out _, out var stationXform))
        {
            if (stationXform.MapID != pos.MapId)
                continue;

            var stationPos = _xform.GetMapCoordinates(stationXform);
            var distSq = (pos.Position - stationPos.Position).LengthSquared();
            if (distSq < nearestDistSq)
            {
                nearest = uid;
                nearestDistSq = distSq;
            }
        }

        return nearest;
    }
}
