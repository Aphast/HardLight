using Content.Server.GameTicking;
using Content.Server.Gateway.Components; // HardLight
using Content.Server.Spawners.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.Gateway.Components;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;

namespace Content.Server.Spawners.EntitySystems;

public sealed class SpawnPointSystem : EntitySystem
{
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly StationSpawningSystem _stationSpawning = default!;
    [Dependency] private readonly TurfSystem _turf = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<PlayerSpawningEvent>(OnPlayerSpawning);
    }

    private void OnPlayerSpawning(PlayerSpawningEvent args)
    {
        if (args.SpawnResult != null)
            return;

        // TODO: Cache all this if it ends up important.
        var points = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        var possiblePositions = new List<EntityCoordinates>();

        while ( points.MoveNext(out var uid, out var spawnPoint, out var xform))
        {
            if (args.Station != null && _stationSystem.GetOwningStation(uid, xform) != args.Station)
                continue;

            // Delta-V: Allow setting a desired SpawnPointType
            if (args.DesiredSpawnPointType != SpawnPointType.Unset)
            {
                var isMatchingJob = spawnPoint.SpawnType == SpawnPointType.Job &&
                    (args.Job == null || spawnPoint.Job == args.Job);

                switch (args.DesiredSpawnPointType)
                {
                    case SpawnPointType.Job when isMatchingJob:
                    case SpawnPointType.LateJoin when spawnPoint.SpawnType == SpawnPointType.LateJoin:
                    case SpawnPointType.Observer when spawnPoint.SpawnType == SpawnPointType.Observer:
                        possiblePositions.Add(xform.Coordinates);
                        break;
                    default:
                        continue;
                }
            }

            if (_gameTicker.RunLevel == GameRunLevel.InRound && spawnPoint.SpawnType == SpawnPointType.LateJoin)
            {
                possiblePositions.Add(xform.Coordinates);
            }

            if (_gameTicker.RunLevel != GameRunLevel.InRound &&
                spawnPoint.SpawnType == SpawnPointType.Job &&
                (args.Job == null || spawnPoint.Job == args.Job))
            {
                possiblePositions.Add(xform.Coordinates);
            }
        }

        if (possiblePositions.Count == 0
            && args.Station is { } station
            && TryFindStationGatewayPosition(station, out var stationGateway)) // HardLight: prefer the station gateway over a random open tile
        {
            possiblePositions.Add(stationGateway);
        }

        if (possiblePositions.Count == 0
            && args.Station is { } stationFb
            && TryFindStationFallbackPosition(stationFb, out var stationFallback))
        {
            possiblePositions.Add(stationFallback);
        }

        if (possiblePositions.Count == 0)
        {
            // Ok we've still not returned, but we need to put them /somewhere/.
            // TODO: Refactor gameticker spawning code so we don't have to do this!
            var points2 = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();

            if (points2.MoveNext(out var spawnPoint, out var xform))
            {
                possiblePositions.Add(xform.Coordinates);
            }
            else
            {
                Log.Error("No spawn points were available!");
                return;
            }
        }

        var spawnLoc = _random.Pick(possiblePositions);

        args.SpawnResult = _stationSpawning.SpawnPlayerMob(
            spawnLoc,
            args.Job,
            args.HumanoidCharacterProfile,
            args.Station,
            session: args.Session); // Frontier
    }

    // HardLight: latejoin/passenger fallback. When a station has no matching spawn points,
    // prefer dropping the player at the station Gateway (every HL station map has one and it
    // is wired up to the docks shuttle), instead of falling through to a random open tile —
    // which is how passengers ended up in atmos / lawyer office / solars (issue #1462).
    private bool TryFindStationGatewayPosition(EntityUid station, out EntityCoordinates coords)
    {
        coords = EntityCoordinates.Invalid;

        if (!TryComp<StationDataComponent>(station, out var stationData))
            return false;

        var query = EntityQueryEnumerator<GatewayComponent, TransformComponent>();
        while (query.MoveNext(out var gatewayUid, out _, out var xform))
        {
            var owning = _stationSystem.GetOwningStation(gatewayUid, xform);
            if (owning != station)
                continue;

            coords = xform.Coordinates;
            return true;
        }

        return false;
    }

    private bool TryFindStationFallbackPosition(EntityUid station, out EntityCoordinates coords)
    {
        coords = EntityCoordinates.Invalid;

        if (!TryComp<StationDataComponent>(station, out var stationData))
            return false;

        var largestGrid = _stationSystem.GetLargestGrid(stationData);
        if (largestGrid is { } gridUid
            && TryFindGridFallbackPosition(gridUid, out coords))
        {
            return true;
        }

        foreach (var memberGrid in stationData.Grids)
        {
            if (memberGrid == largestGrid)
                continue;

            if (TryFindGridFallbackPosition(memberGrid, out coords))
                return true;
        }

        return false;
    }

    private bool TryFindGridFallbackPosition(EntityUid gridUid, out EntityCoordinates coords)
    {
        coords = EntityCoordinates.Invalid;

        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        var candidateTiles = new List<TileRef>();
        foreach (var tile in _mapSystem.GetAllTiles(gridUid, grid))
        {
            if (tile.Tile.IsEmpty || _turf.IsTileBlocked(tile, CollisionGroup.MobMask))
                continue;

            candidateTiles.Add(tile);
        }

        if (candidateTiles.Count == 0)
            return false;

        coords = _turf.GetTileCenter(_random.Pick(candidateTiles));
        return true;
    }
}
