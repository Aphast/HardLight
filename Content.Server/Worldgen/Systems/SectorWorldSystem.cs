using System.Numerics;
using System.Linq;
using Content.Server._Mono.Cleanup;
using Content.Server._NF.RoundNotifications.Events;
using Content.Server._NF.Shuttles.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.Parallax;
using Content.Server.Weather;
using Content.Server.Worldgen.Components;
using Content.Shared.GameTicking;
using Content.Shared.Atmos;
using Content.Shared.Gravity;
using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Shuttles.Components;
using Content.Shared.Weather;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.Worldgen.Systems;

/// <summary>
/// Owns streamed sector metadata, deterministic planet descriptors, and expedition site reservations.
/// </summary>
public sealed class SectorWorldSystem : EntitySystem
{
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly NoiseIndexSystem _noiseIndex = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly BiomeSystem _biome = default!;
    [Dependency] private readonly MapLoaderSystem _loader = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly PhysicsSystem _physics = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;
    [Dependency] private readonly WeatherSystem _weather = default!;

    private static readonly string[] TimeOfDayStates = ["Dawn", "Day", "Dusk", "Night"];
    private readonly string _siteCacheSession = Guid.NewGuid().ToString("N");
    private bool _roundRestartCleanupActive;

    public override void Initialize()
    {
        SubscribeLocalEvent<SectorWorldComponent, ComponentStartup>(OnSectorStartup);
        SubscribeLocalEvent<SectorExpeditionSiteComponent, ComponentShutdown>(OnExpeditionSiteShutdown);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<RoundStartedEvent>(OnRoundStarted);
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _roundRestartCleanupActive = true;
        CleanupHostedSiteCachesForRoundRestart();
        CleanupLayerMapsForRoundRestart();
    }

    private void OnRoundStarted(RoundStartedEvent ev)
    {
        _roundRestartCleanupActive = false;
    }

    private void OnSectorStartup(Entity<SectorWorldComponent> ent, ref ComponentStartup args)
    {
        EnsureInitialized(ent);
    }

    private void OnExpeditionSiteShutdown(Entity<SectorExpeditionSiteComponent> ent, ref ComponentShutdown args)
    {
        CleanupHostedSite(ent);

        if (!TryComp(ent.Comp.SectorMap, out SectorWorldComponent? sector))
            return;

        sector.Reservations.Remove(ent.Owner);
    }

    public void CaptureHostedSiteBaseline(Entity<SectorExpeditionSiteComponent> ent, EntityUid hostGridUid, MapGridComponent grid, Vector2 center, float radius)
    {
        ent.Comp.HostGridUid = hostGridUid;
        ent.Comp.ContentRadius = radius;
        ent.Comp.OriginalTiles.Clear();
        ent.Comp.OriginalEntities.Clear();
        ent.Comp.GeneratedEntities.Clear();
        ent.Comp.CachedChunkTiles.Clear();
        ent.Comp.CachedChunkEntityFiles.Clear();

        foreach (var tile in _mapSystem.GetTilesIntersecting(hostGridUid, grid, new Circle(center, radius), false))
        {
            ent.Comp.OriginalTiles[tile.GridIndices] = tile.Tile;
        }

        if (!TryGetHostedSiteMapUid(hostGridUid, out var mapUid))
            return;

        var radiusSquared = radius * radius;
        var query = AllEntityQuery<TransformComponent>();

        while (query.MoveNext(out var uid, out var xform))
        {
            if (uid == ent.Owner || uid == hostGridUid || uid == mapUid)
                continue;

            if (xform.MapUid != mapUid)
                continue;

            if ((xform.Coordinates.Position - center).LengthSquared() > radiusSquared)
                continue;

            ent.Comp.OriginalEntities.Add(uid);
        }
    }

    public void CaptureHostedSiteGeneratedEntities(Entity<SectorExpeditionSiteComponent> ent, EntityUid hostMapUid, Vector2 center, float radius)
    {
        ent.Comp.GeneratedEntities.Clear();

        var radiusSquared = radius * radius;
        var query = AllEntityQuery<TransformComponent>();

        while (query.MoveNext(out var uid, out var xform))
        {
            if (uid == ent.Owner || uid == ent.Comp.HostGridUid || uid == hostMapUid || ent.Comp.OriginalEntities.Contains(uid))
                continue;

            if (xform.MapUid != hostMapUid)
                continue;

            if ((xform.Coordinates.Position - center).LengthSquared() > radiusSquared)
                continue;

            ent.Comp.GeneratedEntities.Add(uid);
        }
    }

    public void SaveHostedChunkContent(EntityUid gridUid, MapGridComponent grid, Vector2i chunkOrigin, int chunkSize)
    {
        if (_roundRestartCleanupActive)
            return;

        var query = EntityQueryEnumerator<SectorExpeditionSiteComponent>();

        while (query.MoveNext(out var uid, out var site))
        {
            if (site.HostGridUid != gridUid)
                continue;

            if (!DoesSiteIntersectChunk(site, chunkOrigin, chunkSize))
                continue;

            SaveHostedChunkContent((uid, site), gridUid, grid, chunkOrigin, chunkSize);
        }
    }

    public void RestoreHostedChunkContent(EntityUid gridUid, MapGridComponent grid, Vector2i chunkOrigin, int chunkSize)
    {
        var query = EntityQueryEnumerator<SectorExpeditionSiteComponent>();

        while (query.MoveNext(out var uid, out var site))
        {
            if (site.HostGridUid != gridUid)
                continue;

            if (!site.CachedChunkTiles.ContainsKey(chunkOrigin) && !site.CachedChunkEntityFiles.ContainsKey(chunkOrigin))
                continue;

            if (!DoesSiteIntersectChunk(site, chunkOrigin, chunkSize))
                continue;

            RestoreHostedChunkContent((uid, site), gridUid, grid, chunkOrigin);
        }
    }

    public void CleanupHostedSite(Entity<SectorExpeditionSiteComponent> ent)
    {
        var liveEntities = CollectHostedSiteEntities(ent.Comp);

        foreach (var generated in ent.Comp.GeneratedEntities.ToArray())
        {
            liveEntities.Add(generated);
        }

        foreach (var generated in liveEntities)
        {
            if (Exists(generated))
                QueueDel(generated);
        }

        ent.Comp.GeneratedEntities.Clear();

        if (ent.Comp.HostGridUid != EntityUid.Invalid
            && ent.Comp.OriginalTiles.Count > 0
            && TryComp<MapGridComponent>(ent.Comp.HostGridUid, out var grid))
        {
            var tiles = new List<(Vector2i, Tile)>(ent.Comp.OriginalTiles.Count);
            foreach (var (indices, tile) in ent.Comp.OriginalTiles)
            {
                tiles.Add((indices, tile));
            }

            _mapSystem.SetTiles(ent.Comp.HostGridUid, grid, tiles);
        }

        ent.Comp.OriginalTiles.Clear();
        ent.Comp.OriginalEntities.Clear();
        ent.Comp.CachedChunkTiles.Clear();
        ent.Comp.CachedChunkEntityFiles.Clear();
    }

    public void CleanupHostedSite(EntityUid uid, SectorExpeditionSiteComponent component)
    {
        CleanupHostedSite((uid, component));
    }

    private void CleanupHostedSiteCachesForRoundRestart()
    {
        var siteQuery = EntityQueryEnumerator<SectorExpeditionSiteComponent>();
        while (siteQuery.MoveNext(out var uid, out var site))
        {
            CleanupHostedSite((uid, site));
        }

        var cacheRoot = GetHostedSiteCacheRoot();
        if (_resources.UserData.Exists(cacheRoot))
            _resources.UserData.Delete(cacheRoot);
    }

    private void CleanupLayerMapsForRoundRestart()
    {
        var defaultMapUid = _mapSystem.GetMapOrInvalid(_gameTicker.DefaultMap);
        var sectorQuery = EntityQueryEnumerator<SectorWorldComponent>();

        while (sectorQuery.MoveNext(out var sectorUid, out var sector))
        {
            foreach (var loader in sector.StartupLoaders.ToArray())
            {
                if (Exists(loader))
                    QueueDel(loader);
            }

            sector.StartupLoaders.Clear();
            sector.Reservations.Clear();

            foreach (var mapUid in sector.PlanetTypeMaps.Values.Distinct().ToArray())
            {
                DeleteLayerMapForRoundRestart(sectorUid, defaultMapUid, mapUid);
            }

            sector.PlanetTypeMaps.Clear();

            if (sector.FtlMap is { } ftlMap)
                DeleteLayerMapForRoundRestart(sectorUid, defaultMapUid, ftlMap);

            if (sector.ColCommMap is { } colCommMap)
                DeleteLayerMapForRoundRestart(sectorUid, defaultMapUid, colCommMap);

            sector.FtlMap = null;
            sector.ColCommMap = null;
            sector.SpaceMap = sectorUid;
        }
    }

    private void DeleteLayerMapForRoundRestart(EntityUid sectorUid, EntityUid defaultMapUid, EntityUid mapUid)
    {
        if (!Exists(mapUid) || mapUid == sectorUid || mapUid == defaultMapUid)
            return;

        if (!TryComp<MapComponent>(mapUid, out var mapComp))
            return;

        if (mapComp.MapId == _gameTicker.DefaultMap || !_mapSystem.MapExists(mapComp.MapId))
            return;

        _mapSystem.DeleteMap(mapComp.MapId);
    }

    private void SaveHostedChunkContent(Entity<SectorExpeditionSiteComponent> ent, EntityUid gridUid, MapGridComponent grid, Vector2i chunkOrigin, int chunkSize)
    {
        var cachedTiles = new Dictionary<Vector2i, Tile>();

        for (var x = 0; x < chunkSize; x++)
        {
            for (var y = 0; y < chunkSize; y++)
            {
                var indices = new Vector2i(chunkOrigin.X + x, chunkOrigin.Y + y);

                if (!IsTileWithinHostedSite(ent.Comp, indices))
                    continue;

                if (!_mapSystem.TryGetTileRef(gridUid, grid, indices, out var tileRef))
                    continue;

                cachedTiles[indices] = tileRef.Tile;
            }
        }

        if (cachedTiles.Count > 0)
            ent.Comp.CachedChunkTiles[chunkOrigin] = cachedTiles;

        if (TryGetHostedSiteMapUid(gridUid, out var mapUid))
        {
            var generated = CollectHostedChunkEntities(ent.Comp, mapUid, gridUid, grid, chunkOrigin, chunkSize);

            if (generated.Count > 0)
            {
                var cachePath = GetHostedChunkEntityCachePath(ent.Owner, chunkOrigin);
                if (_loader.TrySaveGeneric(generated, cachePath, out _))
                {
                    ent.Comp.CachedChunkEntityFiles[chunkOrigin] = cachePath.ToString();
                }

                foreach (var entity in generated)
                {
                    ent.Comp.GeneratedEntities.Remove(entity);
                    if (Exists(entity))
                        QueueDel(entity);
                }
            }
        }

        if (cachedTiles.Count == 0)
            return;

        var restoreTiles = new List<(Vector2i, Tile)>(cachedTiles.Count);
        foreach (var indices in cachedTiles.Keys)
        {
            restoreTiles.Add((indices, ent.Comp.OriginalTiles.TryGetValue(indices, out var original) ? original : Tile.Empty));
        }

        _mapSystem.SetTiles(gridUid, grid, restoreTiles);
    }

    private void RestoreHostedChunkContent(Entity<SectorExpeditionSiteComponent> ent, EntityUid gridUid, MapGridComponent grid, Vector2i chunkOrigin)
    {
        if (ent.Comp.CachedChunkTiles.Remove(chunkOrigin, out var tiles))
        {
            var restoreTiles = new List<(Vector2i, Tile)>(tiles.Count);
            foreach (var (indices, tile) in tiles)
            {
                restoreTiles.Add((indices, tile));
            }

            _mapSystem.SetTiles(gridUid, grid, restoreTiles);
        }

        if (!ent.Comp.CachedChunkEntityFiles.Remove(chunkOrigin, out var cachePathString))
            return;

        if (!TryGetHostedSiteMapId(gridUid, out var mapId))
            return;

        var loadOptions = new MapLoadOptions
        {
            MergeMap = mapId,
        };

        if (!_loader.TryLoadGeneric(new ResPath(cachePathString), out var result, loadOptions) || result == null)
            return;

        foreach (var entity in result.Entities)
        {
            if (entity != ent.Owner && entity != ent.Comp.HostGridUid)
                ent.Comp.GeneratedEntities.Add(entity);
        }
    }

    private HashSet<EntityUid> CollectHostedSiteEntities(SectorExpeditionSiteComponent site)
    {
        var entities = new HashSet<EntityUid>();

        if (site.HostGridUid == EntityUid.Invalid || !TryGetHostedSiteMapUid(site.HostGridUid, out var mapUid))
            return entities;

        var radius = GetHostedSiteRadius(site);
        var radiusSquared = radius * radius;
        var query = AllEntityQuery<TransformComponent>();

        while (query.MoveNext(out var uid, out var xform))
        {
            if (uid == site.HostGridUid || uid == mapUid || site.OriginalEntities.Contains(uid))
                continue;

            if (xform.MapUid != mapUid)
                continue;

            if ((xform.Coordinates.Position - site.Center).LengthSquared() > radiusSquared)
                continue;

            entities.Add(uid);
        }

        return entities;
    }

    private HashSet<EntityUid> CollectHostedChunkEntities(
        SectorExpeditionSiteComponent site,
        EntityUid mapUid,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i chunkOrigin,
        int chunkSize)
    {
        var entities = new HashSet<EntityUid>();
        var query = AllEntityQuery<TransformComponent>();

        while (query.MoveNext(out var uid, out var xform))
        {
            if (uid == gridUid || uid == mapUid || site.OriginalEntities.Contains(uid))
                continue;

            if (xform.MapUid != mapUid)
                continue;

            var tile = _mapSystem.LocalToTile(gridUid, grid, xform.Coordinates);
            if (!IsChunkTile(tile, chunkOrigin, chunkSize) || !IsTileWithinHostedSite(site, tile))
                continue;

            entities.Add(uid);
        }

        foreach (var entity in entities)
        {
            site.GeneratedEntities.Add(entity);
        }

        return entities;
    }

    private bool DoesSiteIntersectChunk(SectorExpeditionSiteComponent site, Vector2i chunkOrigin, int chunkSize)
    {
        var minX = chunkOrigin.X;
        var maxX = chunkOrigin.X + chunkSize;
        var minY = chunkOrigin.Y;
        var maxY = chunkOrigin.Y + chunkSize;
        var clampedX = Math.Clamp(site.Center.X, minX, maxX);
        var clampedY = Math.Clamp(site.Center.Y, minY, maxY);
        var delta = site.Center - new Vector2(clampedX, clampedY);
        var radius = GetHostedSiteRadius(site);
        return delta.LengthSquared() <= radius * radius;
    }

    private bool IsTileWithinHostedSite(SectorExpeditionSiteComponent site, Vector2i indices)
    {
        var delta = site.Center - (indices + new Vector2(0.5f, 0.5f));
        var radius = GetHostedSiteRadius(site);
        return delta.LengthSquared() <= radius * radius;
    }

    private float GetHostedSiteRadius(SectorExpeditionSiteComponent site)
    {
        return site.ContentRadius > 0f ? site.ContentRadius : site.Radius;
    }

    private bool TryGetHostedSiteMapUid(EntityUid gridUid, out EntityUid mapUid)
    {
        mapUid = EntityUid.Invalid;

        if (HasComp<MapComponent>(gridUid))
        {
            mapUid = gridUid;
            return true;
        }

        var xform = Transform(gridUid);
        if (xform.MapUid is not { } resolved)
            return false;

        mapUid = resolved;
        return true;
    }

    private bool TryGetHostedSiteMapId(EntityUid gridUid, out MapId mapId)
    {
        mapId = MapId.Nullspace;

        if (TryComp<MapComponent>(gridUid, out var mapComp))
        {
            mapId = mapComp.MapId;
            return true;
        }

        var xform = Transform(gridUid);
        if (xform.MapUid is not { } mapUid || !TryComp<MapComponent>(mapUid, out mapComp))
            return false;

        mapId = mapComp.MapId;
        return true;
    }

    private static bool IsChunkTile(Vector2i tile, Vector2i chunkOrigin, int chunkSize)
    {
        return tile.X >= chunkOrigin.X
            && tile.Y >= chunkOrigin.Y
            && tile.X < chunkOrigin.X + chunkSize
            && tile.Y < chunkOrigin.Y + chunkSize;
    }

    private ResPath GetHostedChunkEntityCachePath(EntityUid siteUid, Vector2i chunkOrigin)
    {
        return new ResPath($"/HardLight/hosted-site-cache/{_siteCacheSession}/{siteUid.Id.ToString()}/{chunkOrigin.X}_{chunkOrigin.Y}.yml");
    }

    private ResPath GetHostedSiteCacheRoot()
    {
        return new ResPath($"/HardLight/hosted-site-cache/{_siteCacheSession}");
    }

    public HashSet<MapId> GetRoundEndCleanupMapIds()
    {
        var mapIds = new HashSet<MapId>();

        var sectorQuery = EntityQueryEnumerator<SectorWorldComponent>();
        while (sectorQuery.MoveNext(out _, out var sector))
        {
            foreach (var mapUid in sector.PlanetTypeMaps.Values)
            {
                TryAddRoundEndCleanupMapId(mapIds, mapUid);
            }
        }

        return mapIds;
    }

    private void TryAddRoundEndCleanupMapId(HashSet<MapId> mapIds, EntityUid mapUid)
    {
        if (!Exists(mapUid) || !TryComp<MapComponent>(mapUid, out var mapComp))
            return;

        if (mapComp.MapId == _gameTicker.DefaultMap)
            return;

        mapIds.Add(mapComp.MapId);
    }

    public bool TryGetDefaultSectorMap(out EntityUid sectorMap, out SectorWorldComponent sector)
    {
        sectorMap = EntityUid.Invalid;
        sector = default!;

        if (!_mapSystem.TryGetMap(_gameTicker.DefaultMap, out var mapUid) || mapUid is not { } resolved)
            return false;

        if (!TryComp<SectorWorldComponent>(resolved, out var resolvedSector) || resolvedSector == null)
            return false;

        sector = resolvedSector;
        sectorMap = resolved;
        EnsureInitialized((sectorMap, sector));
        return true;
    }

    public bool TryGetPersistentMap(string? planetTypeId, out EntityUid mapUid, out SectorPlanetDescriptor? planet, SectorWorldComponent? sector = null)
    {
        mapUid = EntityUid.Invalid;
        planet = null;

        if (!TryGetDefaultSectorMap(out var sectorMap, out sector))
            return false;

        EnsureInitialized((sectorMap, sector));

        if (string.IsNullOrWhiteSpace(planetTypeId))
        {
            mapUid = sector.SpaceMap ?? sectorMap;
            return true;
        }

        planet = sector.Planets.FirstOrDefault(candidate => candidate.PlanetTypeId == planetTypeId);
        if (planet == null)
            return false;

        if (!sector.PlanetTypeMaps.TryGetValue(planetTypeId, out mapUid))
            return false;

        return true;
    }

    public bool TryResolvePlanetTypeForBiome(string? biomeTemplateId, out string? planetTypeId, SectorWorldComponent? sector = null)
    {
        planetTypeId = null;

        if (string.IsNullOrWhiteSpace(biomeTemplateId))
            return false;

        if (!TryGetDefaultSectorMap(out var sectorMap, out sector))
            return false;

        EnsureInitialized((sectorMap, sector));
        var match = sector.PlanetTypes.FirstOrDefault(candidate =>
            string.Equals(candidate.BiomeTemplate, biomeTemplateId, StringComparison.OrdinalIgnoreCase)
            || candidate.BiomeAliases.Any(alias => string.Equals(alias, biomeTemplateId, StringComparison.OrdinalIgnoreCase)));

        if (match == null)
            return false;

        planetTypeId = match.Id;
        return true;
    }

    public bool TryGetPlanetAtPosition(EntityUid sectorMap, Vector2 worldPos, out SectorPlanetDescriptor planet, SectorWorldComponent? sector = null)
    {
        planet = default!;

        if (!Resolve(sectorMap, ref sector, false))
            return false;

        if (sector == null)
            return false;

        EnsureInitialized((sectorMap, sector));

        foreach (var candidate in sector.Planets)
        {
            if ((worldPos - candidate.Center).LengthSquared() <= candidate.Radius * candidate.Radius)
            {
                planet = candidate;
                return true;
            }
        }

        return false;
    }

    public bool TryGetSectorGrid(EntityUid sectorMap, out EntityUid gridUid, SectorWorldComponent? sector = null)
    {
        gridUid = EntityUid.Invalid;

        if (!Resolve(sectorMap, ref sector, false))
            return false;

        EnsureInitialized((sectorMap, sector));

        if (sector == null || sector.SectorGrid is not { } resolvedGrid || !Exists(resolvedGrid))
            return false;

        gridUid = resolvedGrid;
        return true;
    }

    public bool TryGetSurfaceTile(SectorPlanetDescriptor planet, out string tileId)
    {
        tileId = planet.SurfaceTile;
        return _tileDefs.TryGetDefinition(tileId, out _);
    }

    public bool IsSolidAt(EntityUid sectorMap, EntityUid noiseHolder, SectorChunkCarverComponent carver, Vector2 worldPos, out SectorPlanetDescriptor planet)
    {
        if (TryComp<SectorWorldComponent>(sectorMap, out SectorWorldComponent? sectorComp) &&
            sectorComp != null &&
            worldPos.LengthSquared() <= sectorComp.CentralClearRadius * sectorComp.CentralClearRadius)
        {
            planet = default!;
            return false;
        }

        var chunkCoords = SharedMapSystem.GetChunkIndices(worldPos, WorldGen.ChunkSize);
        if (sectorComp == null)
        {
            planet = default!;
            return false;
        }

        planet = GetSectorAsteroidDescriptor(sectorComp, chunkCoords);

        var localPos = worldPos;
        var chunkSample = new Vector2(chunkCoords.X + 0.5f, chunkCoords.Y + 0.5f) / MathF.Max(carver.ChunkFieldScale, 1f);
        var sparseSample = localPos / MathF.Max(carver.SparseFieldScale, 1f);
        var islandSample = localPos / MathF.Max(carver.IslandFieldScale, 1f);
        var detailSample = localPos / MathF.Max(carver.DetailFieldScale, 1f);

        var chunkPresence = _noiseIndex.Evaluate(noiseHolder, carver.IslandNoiseChannel, chunkSample * 0.57f + new Vector2(17.5f, -12.25f));
        if (chunkPresence < carver.ChunkThreshold)
            return false;

        var sparse = _noiseIndex.Evaluate(noiseHolder, carver.IslandNoiseChannel, sparseSample * 0.73f + new Vector2(-11.75f, 6.25f));
        if (sparse < carver.SparseThreshold)
            return false;

        var density = _noiseIndex.Evaluate(noiseHolder, carver.DensityNoiseChannel, islandSample * 1.07f + new Vector2(3.25f, -1.75f));
        var carve = _noiseIndex.Evaluate(noiseHolder, carver.CarveNoiseChannel, detailSample * 1.33f + new Vector2(7.5f, -4.25f));
        var islands = _noiseIndex.Evaluate(noiseHolder, carver.IslandNoiseChannel, islandSample * 1.61f + new Vector2(-3.75f, 5.5f));

        var densityBias = 0f;

        var sparseStrength = (sparse - carver.SparseThreshold) / MathF.Max(1f - carver.SparseThreshold, 0.001f);
        sparseStrength = Math.Clamp(sparseStrength, 0f, 1f);
        sparseStrength = MathF.Pow(sparseStrength, carver.DensitySharpness);

        var ridge = 1f - MathF.Abs(carve - 0.5f) * 2f;
        ridge = Math.Clamp(ridge, 0f, 1f);

        var islandMass = islands * 0.42f + sparseStrength * 0.26f + ridge * 0.12f;
        var signedDensity = density * 0.38f + islandMass - densityBias - 0.12f;
        var baseMass = islands >= carver.IslandThreshold - 0.08f
            && signedDensity >= carver.DensityThreshold - 0.04f;
        var carvedOut = carve >= carver.CarveRange.X && carve <= carver.CarveRange.Y && islandMass < 0.92f;

        return baseMass && !carvedOut;
    }

    private SectorPlanetDescriptor GetSectorAsteroidDescriptor(SectorWorldComponent sector, Vector2i chunkCoords)
    {
        if (sector.Planets.Count == 0)
        {
            return new SectorPlanetDescriptor
            {
                SurfaceTile = "FloorSteel",
            };
        }

        var hash = HashCode.Combine(sector.UniverseSeed, chunkCoords.X, chunkCoords.Y);
        var index = Math.Abs(hash % sector.Planets.Count);
        return sector.Planets[index];
    }

    public bool TryReserveExpeditionSite(int seed, EntityUid expeditionUid, string? planetTypeId, out SectorExpeditionPlacement placement)
    {
        placement = default!;

        if (!TryGetDefaultSectorMap(out var sectorMap, out var sector))
            return false;

        var rng = new Random(seed);
        var planets = sector.Planets
            .Where(planet => string.IsNullOrWhiteSpace(planetTypeId) || planet.PlanetTypeId == planetTypeId)
            .OrderBy(_ => rng.Next())
            .ToList();
        var reservationRadius = sector.MissionReservationRadius;

        foreach (var planet in planets)
        {
            if (!TryGetPersistentMap(planet.PlanetTypeId, out var targetMap, out _ , sector))
                continue;

            var placementOrigin = targetMap == (sector.SpaceMap ?? sectorMap)
                ? planet.Center
                : Vector2.Zero;

            for (var attempt = 0; attempt < 32; attempt++)
            {
                var angle = rng.NextSingle() * MathF.Tau;
                var distance = MathF.Sqrt(rng.NextSingle()) * MathF.Max(planet.Radius - reservationRadius, 64f);
                var candidate = placementOrigin + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;

                if (!IsReservationFree(sector, candidate, reservationRadius))
                    continue;

                var reservation = new SectorExpeditionReservation
                {
                    ExpeditionUid = expeditionUid,
                    PlanetId = planet.PlanetId,
                    Center = candidate,
                    Radius = reservationRadius,
                };

                sector.Reservations[expeditionUid] = reservation;
                placement = new SectorExpeditionPlacement
                {
                    SectorMap = targetMap,
                    PlanetTypeId = planet.PlanetTypeId,
                    Center = candidate,
                    ReservationRadius = reservationRadius,
                    Planet = planet,
                };
                return true;
            }
        }

        return false;
    }

    private bool IsReservationFree(SectorWorldComponent sector, Vector2 center, float radius)
    {
        foreach (var reservation in sector.Reservations.Values)
        {
            var minDistance = radius + reservation.Radius + sector.MissionReservationPadding;
            if ((reservation.Center - center).LengthSquared() < minDistance * minDistance)
                return false;
        }

        return true;
    }

    private void EnsureInitialized(Entity<SectorWorldComponent> ent)
    {
        if (ent.Comp.SpaceMap == null || !Exists(ent.Comp.SpaceMap.Value))
            ent.Comp.SpaceMap = ent.Owner;

        if ((ent.Comp.SectorGrid == null || !Exists(ent.Comp.SectorGrid.Value)) && TryComp<MapComponent>(ent.Owner, out var mapComp))
        {
            var sectorGrid = _mapManager.CreateGridEntity(mapComp.MapId);
            ent.Comp.SectorGrid = sectorGrid.Owner;
            sectorGrid.Comp.CanSplit = false;
            EnsureComp<CleanupImmuneComponent>(sectorGrid.Owner);
            _metaData.SetEntityName(sectorGrid.Owner, $"{MetaData(ent.Owner).EntityName} Sector Grid");
        }

        if (ent.Comp.SectorGrid is { } existingSectorGrid)
            EnsurePersistentWorldGrid(existingSectorGrid);

        if (ent.Comp.UniverseSeed == 0)
            ent.Comp.UniverseSeed = _random.Next(1, int.MaxValue);

        if (ent.Comp.Planets.Count == 0 && ent.Comp.PlanetTypes.Count > 0)
        {
            var rng = new Random(ent.Comp.UniverseSeed);
            var ringStep = 2400f;

            for (var index = 0; index < ent.Comp.PlanetTypes.Count; index++)
            {
                var type = ent.Comp.PlanetTypes[index];
                var radius = MathHelper.Lerp(type.MinRadius, type.MaxRadius, rng.NextSingle());
                var distance = 1800f + index * ringStep + rng.NextSingle() * 900f;
                var angle = (MathF.Tau / ent.Comp.PlanetTypes.Count) * index + (rng.NextSingle() - 0.5f) * 0.45f;
                var center = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
                var tileId = type.SurfaceTiles.Count > 0
                    ? type.SurfaceTiles[rng.Next(type.SurfaceTiles.Count)]
                    : "FloorSteel";

                if (!_proto.TryIndex<BiomeTemplatePrototype>(type.BiomeTemplate, out _))
                    continue;

                ent.Comp.Planets.Add(new SectorPlanetDescriptor
                {
                    PlanetId = $"{type.Id}-{index + 1}",
                    Name = $"{type.Name} {index + 1}",
                    PlanetTypeId = type.Id,
                    BiomeTemplate = type.BiomeTemplate,
                    SurfaceTile = tileId,
                    Center = center,
                    Radius = radius,
                    Seed = rng.Next(),
                    Temperature = MathHelper.Lerp(type.MinTemperature, type.MaxTemperature, rng.NextSingle()),
                    Oxygen = MathHelper.Lerp(type.MinOxygen, type.MaxOxygen, rng.NextSingle()),
                    Nitrogen = MathHelper.Lerp(type.MinNitrogen, type.MaxNitrogen, rng.NextSingle()),
                    CarbonDioxide = MathHelper.Lerp(type.MinCarbonDioxide, type.MaxCarbonDioxide, rng.NextSingle()),
                    TimeOfDay = TimeOfDayStates[rng.Next(TimeOfDayStates.Length)],
                    WeatherPrototype = rng.NextSingle() < 0.5f ? null : type.WeatherPrototype,
                });
            }
        }

        EnsurePersistentLayerMaps(ent);
        EnsureStartupPlanetLoaders(ent);
    }

    private void EnsureStartupPlanetLoaders(Entity<SectorWorldComponent> ent)
    {
        if (ent.Comp.StartupLoaders.Count == 0)
            return;

        foreach (var loader in ent.Comp.StartupLoaders)
        {
            if (Exists(loader))
                QueueDel(loader);
        }

        ent.Comp.StartupLoaders.Clear();
    }

    private void EnsurePersistentLayerMaps(Entity<SectorWorldComponent> ent)
    {
        if (ent.Comp.FtlMap is { } ftlMap && !Exists(ftlMap))
            ent.Comp.FtlMap = null;

        CleanupLegacyColCommLayerMap(ent);

        var invalidPlanetMaps = ent.Comp.PlanetTypeMaps
            .Where(pair => !Exists(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var planetTypeId in invalidPlanetMaps)
        {
            ent.Comp.PlanetTypeMaps.Remove(planetTypeId);
        }

        ent.Comp.FtlMap ??= CreateLayerMap($"{MetaData(ent.Owner).EntityName} FTL", space: true, gravity: false);

        EnsurePersistentWorldGrid(ent.Comp.FtlMap.Value);

        foreach (var planet in ent.Comp.Planets)
        {
            if (ent.Comp.PlanetTypeMaps.ContainsKey(planet.PlanetTypeId))
            {
                EnsurePersistentWorldGrid(ent.Comp.PlanetTypeMaps[planet.PlanetTypeId]);
                continue;
            }

            ent.Comp.PlanetTypeMaps[planet.PlanetTypeId] = CreateLayerMap(
                $"{planet.Name} Surface",
                space: false,
                gravity: true,
                mixture: CreatePlanetMixture(planet),
                timeOfDay: planet.TimeOfDay,
                weatherPrototype: planet.WeatherPrototype,
                biomeTemplateId: planet.BiomeTemplate,
                biomeSeed: planet.Seed);

            EnsurePersistentWorldGrid(ent.Comp.PlanetTypeMaps[planet.PlanetTypeId]);
        }
    }

    private void CleanupLegacyColCommLayerMap(Entity<SectorWorldComponent> ent)
    {
        if (ent.Comp.ColCommMap is not { } colCommMap)
            return;

        ent.Comp.ColCommMap = null;

        if (!Exists(colCommMap) || colCommMap == ent.Owner)
            return;

        var defaultMapUid = _mapSystem.GetMapOrInvalid(_gameTicker.DefaultMap);
        if (colCommMap == defaultMapUid)
            return;

        if (TryComp<MapComponent>(colCommMap, out var mapComp))
        {
            _mapSystem.DeleteMap(mapComp.MapId);
            return;
        }

        QueueDel(colCommMap);
    }

    private void EnsurePersistentWorldGrid(EntityUid mapOrGridUid)
    {
        if (!Exists(mapOrGridUid))
            return;

        EnsureComp<CleanupImmuneComponent>(mapOrGridUid);
        EnsureComp<PreventGridAnchorChangesComponent>(mapOrGridUid);

        var physics = EnsureComp<PhysicsComponent>(mapOrGridUid);
        if (physics.BodyType != BodyType.Static)
            _physics.SetBodyType(mapOrGridUid, BodyType.Static, body: physics);

        _physics.SetBodyStatus(mapOrGridUid, physics, BodyStatus.OnGround);
        _physics.SetFixedRotation(mapOrGridUid, true, body: physics);

        if (TryComp<MapGridComponent>(mapOrGridUid, out var grid))
            grid.CanSplit = false;
    }

    private EntityUid CreateLayerMap(
        string name,
        bool space,
        bool gravity,
        GasMixture? mixture = null,
        string? timeOfDay = null,
        string? weatherPrototype = null,
        string? biomeTemplateId = null,
        int? biomeSeed = null)
    {
        var mapUid = _mapSystem.CreateMap(out _);
        EnsureComp<FTLMapComponent>(mapUid);
        EnsureComp<CleanupImmuneComponent>(mapUid);
        _metaData.SetEntityName(mapUid, name);

        if (!space && !string.IsNullOrWhiteSpace(biomeTemplateId) && _proto.TryIndex<BiomeTemplatePrototype>(biomeTemplateId, out var biomeTemplate))
        {
            _biome.EnsurePlanet(mapUid, biomeTemplate, biomeSeed, mapLight: GetAmbientLightForTimeOfDay(timeOfDay));
        }

        if (mixture != null)
            _atmosphere.SetMapAtmosphere(mapUid, space, mixture);
        else if (space)
            _atmosphere.SetMapAtmosphere(mapUid, true, GasMixture.SpaceGas);

        var gravityComp = EnsureComp<GravityComponent>(mapUid);
        gravityComp.Enabled = gravity;
        gravityComp.Inherent = gravity;

        var light = EnsureComp<MapLightComponent>(mapUid);
        light.AmbientLightColor = GetAmbientLightForTimeOfDay(timeOfDay);

        EnsureComp<LightCycleComponent>(mapUid);
        EnsureComp<SunShadowComponent>(mapUid);
        EnsureComp<SunShadowCycleComponent>(mapUid);

        var resolvedWeatherPrototype = ResolveWeatherPrototype(weatherPrototype);

        if (!string.IsNullOrWhiteSpace(resolvedWeatherPrototype) &&
            TryComp<MapComponent>(mapUid, out var mapComp))
        {
            _weather.TrySetWeather(mapComp.MapId, resolvedWeatherPrototype, out _);
        }

        return mapUid;
    }

    private static GasMixture CreateStandardAirMixture()
    {
        var moles = new float[Atmospherics.AdjustedNumberOfGases];
        moles[(int) Gas.Oxygen] = 21.824779f;
        moles[(int) Gas.Nitrogen] = 82.10312f;
        return new GasMixture(moles, Atmospherics.T20C);
    }

    private static GasMixture CreatePlanetMixture(SectorPlanetDescriptor planet)
    {
        var moles = new float[Atmospherics.AdjustedNumberOfGases];
        moles[(int) Gas.Oxygen] = MathF.Max(planet.Oxygen, 0f);
        moles[(int) Gas.Nitrogen] = MathF.Max(planet.Nitrogen, 0f);
        moles[(int) Gas.CarbonDioxide] = MathF.Max(planet.CarbonDioxide, 0f);
        return new GasMixture(moles, MathF.Max(planet.Temperature, Atmospherics.TCMB));
    }

    private static Color GetAmbientLightForTimeOfDay(string? timeOfDay)
    {
        return timeOfDay switch
        {
            "Night" => Color.FromHex("#2B3143"),
            "Dusk" => Color.FromHex("#A34931"),
            "Day" => Color.FromHex("#E6CB8B"),
            _ => Color.FromHex("#D8B059"),
        };
    }

    private string? ResolveWeatherPrototype(string? weatherPrototype)
    {
        if (string.IsNullOrWhiteSpace(weatherPrototype))
            return null;

        if (_proto.HasIndex<EntityPrototype>(weatherPrototype))
            return weatherPrototype;

        var weatherEntityId = $"Weather{weatherPrototype}";
        if (_proto.HasIndex<EntityPrototype>(weatherEntityId))
            return weatherEntityId;

        return weatherPrototype;
    }
}