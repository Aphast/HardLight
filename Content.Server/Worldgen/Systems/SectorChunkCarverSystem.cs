using System.Numerics;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using Robust.Server.GameObjects;
using Content.Server._NF.RoundNotifications.Events;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Tools;
using Content.Shared.GameTicking;
using Content.Shared.Maps;
using Content.Shared.Storage;
using Content.Shared.Worldgen.Prototypes;
using Robust.Shared.Maths;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Worldgen.Systems;

/// <summary>
/// Materializes streamed sector chunk geometry into a single persistent grid on the sector map.
/// </summary>
public sealed class SectorChunkCarverSystem : EntitySystem
{
    private static readonly string[] OreSuffixes =
    [
        "Coal",
        "Tin",
        "Quartz",
        "Salt",
        "Gold",
        "Silver",
        "Plasma",
        "Uranium",
        "Bananium",
        "ArtifactFragment",
        "Bluespace",
    ];

    [Dependency] private readonly SectorWorldSystem _sectorWorld = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private string _cacheDirectory = string.Empty;
    private readonly Dictionary<string, Dictionary<string, EntitySpawnCollectionCache>> _biomeCaches = new();
    private bool _roundRestartCleanupActive;

    public override void Initialize()
    {
        ResetCacheDirectory();

        SubscribeLocalEvent<SectorChunkCarverComponent, WorldChunkLoadedEvent>(OnChunkLoaded);
        SubscribeLocalEvent<SectorChunkCarverComponent, WorldChunkUnloadedEvent>(OnChunkUnloaded);
        SubscribeLocalEvent<SectorChunkCarverComponent, ComponentShutdown>(OnChunkShutdown);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<RoundStartedEvent>(OnRoundStarted);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        DeleteCacheDirectory();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _roundRestartCleanupActive = true;
        ResetAllChunkCachePaths();
        ResetCacheDirectory();
    }

    private void OnRoundStarted(RoundStartedEvent ev)
    {
        _roundRestartCleanupActive = false;
    }

    private void ResetAllChunkCachePaths()
    {
        var query = EntityQueryEnumerator<SectorChunkCarverComponent>();
        while (query.MoveNext(out _, out var carver))
        {
            carver.CacheFilePath = null;
        }
    }

    private void ResetCacheDirectory()
    {
        DeleteCacheDirectory();

        var cacheRunId = Path.GetFileName(Guid.NewGuid().ToString("N"));
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "HardLight", "sector-chunk-cache", cacheRunId);
        Directory.CreateDirectory(_cacheDirectory);
    }

    private void DeleteCacheDirectory()
    {

        try
        {
            if (!string.IsNullOrWhiteSpace(_cacheDirectory) && Directory.Exists(_cacheDirectory))
                Directory.Delete(_cacheDirectory, true);
        }
        catch (IOException)
        {
            // Temp cache cleanup is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Temp cache cleanup is best-effort.
        }
    }

    private void OnChunkLoaded(Entity<SectorChunkCarverComponent> ent, ref WorldChunkLoadedEvent args)
    {
        if (ent.Comp.Materialized)
            return;

        if (!TryComp<WorldChunkComponent>(args.Chunk, out var chunk))
            return;

        if (!TryComp<SectorWorldComponent>(chunk.Map, out var sector))
            return;

        if (!_sectorWorld.TryGetSectorGrid(chunk.Map, out var gridUid, sector))
            return;

        var grid = EnsureComp<MapGridComponent>(gridUid);
        ent.Comp.MaterializedGrid = gridUid;
        ent.Comp.GeneratedTiles.Clear();
        ent.Comp.GeneratedEntities.Clear();
        var blockedGrids = GetBlockingGrids(chunk.Map, gridUid, chunk);
        var chunkBiome = GetChunkBiome(ent.Comp, sector, chunk.Coordinates);

        if (!TryComp<MapComponent>(chunk.Map, out MapComponent? mapComp) || mapComp == null)
            return;

        var sectorMapId = mapComp.MapId;
        var chunkOrigin = chunk.Coordinates * WorldGen.ChunkSize;

        if (TryRestoreChunkFromCache((ent.Owner, ent.Comp), chunk, gridUid, grid, chunkBiome))
        {
            _sectorWorld.RestoreHostedChunkContent(gridUid, grid, chunkOrigin, WorldGen.ChunkSize);
            ent.Comp.Materialized = true;
            return;
        }

        var tiles = new List<(Vector2i, Tile)>(WorldGen.ChunkSize * WorldGen.ChunkSize / 3);

        for (var x = 0; x < WorldGen.ChunkSize; x++)
        {
            for (var y = 0; y < WorldGen.ChunkSize; y++)
            {
                var indices = chunkOrigin + new Vector2i(x, y);
                var worldPos = indices + new Vector2(0.5f, 0.5f);

                if (IsBlockedByOtherGrid(worldPos, sectorMapId, blockedGrids))
                    continue;

                if (!_sectorWorld.IsSolidAt(chunk.Map, ent.Owner, ent.Comp, worldPos, out _))
                    continue;

                var tileId = GetChunkFloorTileId(chunkBiome, sector, indices);

                var tileDef = (ContentTileDefinition) _tileDefs[tileId];
                tiles.Add((indices, new Tile(tileDef.TileId)));
                ent.Comp.GeneratedTiles.Add(indices);
            }
        }

        if (tiles.Count > 0)
            _mapSystem.SetTiles(gridUid, grid, tiles);

        SpawnChunkEntities((ent.Owner, ent.Comp), gridUid, grid, chunkBiome);
    _sectorWorld.RestoreHostedChunkContent(gridUid, grid, chunkOrigin, WorldGen.ChunkSize);

        ent.Comp.Materialized = true;
    }

    private void OnChunkUnloaded(Entity<SectorChunkCarverComponent> ent, ref WorldChunkUnloadedEvent args)
    {
        if (_roundRestartCleanupActive)
            return;

        if (!ent.Comp.Materialized || ent.Comp.GeneratedTiles.Count == 0)
            return;

        if (!TryComp<WorldChunkComponent>(args.Chunk, out var chunk))
            return;

        if (!TryComp<SectorWorldComponent>(chunk.Map, out var sector))
            return;

        if (!_sectorWorld.TryGetSectorGrid(chunk.Map, out var gridUid, sector))
            return;

        var grid = EnsureComp<MapGridComponent>(gridUid);
        var chunkOrigin = chunk.Coordinates * WorldGen.ChunkSize;

        _sectorWorld.SaveHostedChunkContent(gridUid, grid, chunkOrigin, WorldGen.ChunkSize);

        SaveChunkToCache((ent.Owner, ent.Comp), gridUid, grid, chunk);

        foreach (var generated in ent.Comp.GeneratedEntities)
        {
            if (Exists(generated))
                QueueDel(generated);
        }

        ent.Comp.GeneratedEntities.Clear();

        var tiles = new List<(Vector2i, Tile)>(ent.Comp.GeneratedTiles.Count);
        foreach (var indices in ent.Comp.GeneratedTiles)
        {
            tiles.Add((indices, Tile.Empty));
        }

        _mapSystem.SetTiles(gridUid, grid, tiles);
        ent.Comp.GeneratedTiles.Clear();
        ent.Comp.Materialized = false;
        ent.Comp.MaterializedGrid = EntityUid.Invalid;
    }

    private void OnChunkShutdown(Entity<SectorChunkCarverComponent> ent, ref ComponentShutdown args)
    {
        if (_roundRestartCleanupActive)
            return;

        if (!ent.Comp.Materialized || ent.Comp.GeneratedTiles.Count == 0)
            return;

        if (!TryComp<WorldChunkComponent>(ent.Owner, out var chunk))
            return;

        if (ent.Comp.MaterializedGrid == EntityUid.Invalid || !TryComp(ent.Comp.MaterializedGrid, out MapGridComponent? grid))
            return;

        SaveChunkToCache(ent, ent.Comp.MaterializedGrid, grid, chunk);
    }

    private void SaveChunkToCache(Entity<SectorChunkCarverComponent> ent, EntityUid gridUid, MapGridComponent grid, WorldChunkComponent chunk)
    {
        var cachePath = GetCachePath(ent.Owner, chunk);
        ent.Comp.CacheFilePath = cachePath;

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        var builder = new StringBuilder();
        builder.AppendLine("v3");

        foreach (var indices in ent.Comp.GeneratedTiles)
        {
            var tileRef = _mapSystem.GetTileRef(gridUid, grid, indices);
            if (tileRef.Tile.IsEmpty)
                continue;

            var tileId = tileRef.Tile.GetContentTileDefinition(_tileDefs).ID;
            builder.Append('t')
                .Append(',')
                .Append(indices.X)
                .Append(',')
                .Append(indices.Y)
                .Append(',')
                .Append(tileId)
                .AppendLine();
        }

        foreach (var generated in ent.Comp.GeneratedEntities)
        {
            if (!Exists(generated))
                continue;

            var meta = MetaData(generated);
            if (meta.EntityPrototype == null || meta.EntityLifeStage >= EntityLifeStage.Terminating)
                continue;

            var xform = Transform(generated);
            if (xform.GridUid != gridUid)
                continue;

            builder.Append('e')
                .Append(',')
                .Append(xform.Coordinates.Position.X.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(xform.Coordinates.Position.Y.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(meta.EntityPrototype.ID)
                .Append(',')
                .Append(xform.LocalRotation.Theta.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(xform.Anchored)
                .AppendLine();
        }

        File.WriteAllText(cachePath, builder.ToString());
    }

    private bool TryRestoreChunkFromCache(Entity<SectorChunkCarverComponent> ent, WorldChunkComponent chunk, EntityUid gridUid, MapGridComponent grid, SectorAsteroidBiomePrototype? chunkBiome)
    {
        var cachePath = ent.Comp.CacheFilePath ?? GetCachePath(ent.Owner, chunk);
        ent.Comp.CacheFilePath = cachePath;

        if (!File.Exists(cachePath))
            return false;

        var tilePlacements = new List<(Vector2i, Tile)>();
        var entityPlacements = new List<ChunkEntityMutationRecord>();
        var cacheVersion = 1;

        foreach (var line in File.ReadLines(cachePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line is "v1" or "v2" or "v3")
            {
                cacheVersion = line[1] - '0';
                continue;
            }

            var parts = line.Split(',');

            if (parts.Length == 3)
            {
                RestoreCachedTile(parts[0], parts[1], parts[2], tilePlacements, ent.Comp.GeneratedTiles);
                continue;
            }

            switch (parts[0])
            {
                case "t":
                    if (parts.Length != 4)
                        continue;

                    RestoreCachedTile(parts[1], parts[2], parts[3], tilePlacements, ent.Comp.GeneratedTiles);
                    break;
                case "e":
                    if (cacheVersion >= 3)
                    {
                        if (parts.Length != 6
                            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var entityPosX)
                            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var entityPosY)
                            || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var entityRotation)
                            || !bool.TryParse(parts[5], out var entityAnchored))
                        {
                            continue;
                        }

                        entityPlacements.Add(new ChunkEntityMutationRecord(
                            new Vector2(entityPosX, entityPosY),
                            parts[3],
                            entityRotation,
                            entityAnchored));
                        continue;
                    }

                    if (parts.Length != 4
                        || !int.TryParse(parts[1], out var entityX)
                        || !int.TryParse(parts[2], out var entityY))
                    {
                        continue;
                    }

                    entityPlacements.Add(new ChunkEntityMutationRecord(
                        new Vector2(entityX + 0.5f, entityY + 0.5f),
                        parts[3],
                        0f,
                        ChunkEntityMutationRules.ShouldAnchor(parts[3])));
                    break;
            }
        }

        var authoritativeSnapshot = cacheVersion >= 3;
        if (!authoritativeSnapshot && tilePlacements.Count == 0)
            return false;

        if (tilePlacements.Count > 0)
            _mapSystem.SetTiles(gridUid, grid, tilePlacements);

        if (entityPlacements.Count > 0)
        {
            foreach (var entityPlacement in entityPlacements.Where(ep => _proto.HasIndex<EntityPrototype>(ep.PrototypeId)))
            {
                var indices = _mapSystem.TileIndicesFor(gridUid, grid, new EntityCoordinates(gridUid, entityPlacement.LocalPosition));
                ClearChunkMaterialEntitiesAtTile((ent.Owner, ent.Comp), gridUid, grid, indices);
                SpawnTrackedChunkEntity((ent.Owner, ent.Comp), gridUid, grid, entityPlacement.LocalPosition, entityPlacement.PrototypeId, new Angle(entityPlacement.Rotation), entityPlacement.Anchored);
            }
        }
        else if (!authoritativeSnapshot)
        {
            SpawnChunkEntities((ent.Owner, ent.Comp), gridUid, grid, chunkBiome);
        }

        return true;
    }

    private void RestoreCachedTile(string xText, string yText, string tileId, List<(Vector2i, Tile)> tilePlacements, HashSet<Vector2i> generatedTiles)
    {
        if (!int.TryParse(xText, out var x) || !int.TryParse(yText, out var y))
            return;

        if (!_tileDefs.TryGetDefinition(tileId, out var tileDefBase) || tileDefBase is not ContentTileDefinition tileDef)
            return;

        var indices = new Vector2i(x, y);
        tilePlacements.Add((indices, new Tile(tileDef.TileId)));
        generatedTiles.Add(indices);
    }

    private string GetCachePath(EntityUid chunkUid, WorldChunkComponent chunk)
    {
        return Path.Join(_cacheDirectory, $"chunk_{chunkUid}_{chunk.Coordinates.X}_{chunk.Coordinates.Y}.cache");
    }

    private SectorAsteroidBiomePrototype? GetChunkBiome(SectorChunkCarverComponent carver, SectorWorldComponent sector, Vector2i chunkCoords)
    {
        if (carver.Biomes.Count == 0)
            return null;

        var index = Math.Abs(HashCode.Combine(sector.UniverseSeed, chunkCoords.X, chunkCoords.Y) % carver.Biomes.Count);
        var biomeId = carver.Biomes[index];
        return _proto.TryIndex<SectorAsteroidBiomePrototype>(biomeId, out var biome) ? biome : null;
    }

    private string GetChunkFloorTileId(SectorAsteroidBiomePrototype? biome, SectorWorldComponent sector, Vector2i indices)
    {
        if (biome == null || biome.FloorTiles.Count == 0)
            return "FloorSteel";

        var index = Math.Abs(HashCode.Combine(sector.UniverseSeed, indices.X, indices.Y) % biome.FloorTiles.Count);
        return biome.FloorTiles[index];
    }

    private void SpawnChunkEntities(Entity<SectorChunkCarverComponent> ent, EntityUid gridUid, MapGridComponent grid, SectorAsteroidBiomePrototype? biome)
    {
        var spawns = new List<string?>(4);

        foreach (var indices in ent.Comp.GeneratedTiles)
        {
            var tile = _mapSystem.GetTileRef(gridUid, grid, indices);
            if (tile.Tile.IsEmpty)
                continue;

            var tileId = tile.Tile.GetContentTileDefinition(_tileDefs).ID;
            var handledByBiome = false;
            ClearChunkMaterialEntitiesAtTile(ent, gridUid, grid, indices);

            var biomeCache = GetBiomeCache(biome);
            if (biomeCache != null && biomeCache.TryGetValue(tileId, out var cache))
            {
                handledByBiome = true;
                spawns.Clear();
                cache.GetSpawns(_random, ref spawns);

                foreach (var prototype in spawns)
                {
                    if (prototype is not { } prototypeId || !_proto.HasIndex<EntityPrototype>(prototypeId))
                        continue;

                    SpawnTrackedTileEntity(ent, gridUid, grid, indices, prototypeId);
                }
            }

            if (handledByBiome)
                continue;

            if (!TryGetPlanetWallPrototype(gridUid, grid, indices, out var wallPrototype))
                continue;

            SpawnTrackedTileEntity(ent, gridUid, grid, indices, wallPrototype);
        }
    }

    private void ClearChunkMaterialEntitiesAtTile(Entity<SectorChunkCarverComponent> ent, EntityUid gridUid, MapGridComponent grid, Vector2i indices)
    {
        var tileRef = _mapSystem.GetTileRef(gridUid, grid, indices);

        foreach (var entity in _lookup.GetLocalEntitiesIntersecting(tileRef, flags: LookupFlags.Dynamic | LookupFlags.Static | LookupFlags.StaticSundries | LookupFlags.Sundries | LookupFlags.Approximate))
        {
            if (entity == gridUid)
                continue;

            var meta = MetaData(entity);
            if (meta.EntityPrototype == null)
                continue;

            if (!IsChunkMaterialPrototype(meta.EntityPrototype.ID))
                continue;

            ent.Comp.GeneratedEntities.Remove(entity);

            if (Exists(entity))
                QueueDel(entity);
        }
    }

    private void SpawnTrackedTileEntity(Entity<SectorChunkCarverComponent> ent, EntityUid gridUid, MapGridComponent grid, Vector2i indices, string prototypeId)
    {
        SpawnTrackedChunkEntity(ent, gridUid, grid, indices + new Vector2(0.5f, 0.5f), prototypeId);
    }

    private void SpawnTrackedChunkEntity(
        Entity<SectorChunkCarverComponent> ent,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2 localPosition,
        string prototypeId,
        Angle? rotation = null,
        bool? anchored = null)
    {
        var coordinates = new EntityCoordinates(gridUid, localPosition);
        var indices = _mapSystem.TileIndicesFor(gridUid, grid, coordinates);
        var before = GetTileEntities(gridUid, grid, indices);
        var spawned = Spawn(prototypeId, _transform.ToMapCoordinates(coordinates));
        _transform.SetCoordinates(spawned, coordinates);
        if (rotation != null)
            _transform.SetLocalRotation(spawned, rotation.Value);

        var after = GetTileEntities(gridUid, grid, indices);

        foreach (var entity in after.Where(entity => !before.Contains(entity)))
        {
            var meta = MetaData(entity);
            if (meta.EntityPrototype == null)
                continue;

            if (IsTransientChunkSpawnerPrototype(meta.EntityPrototype.ID))
            {
                if (Exists(entity))
                    QueueDel(entity);

                continue;
            }

            ApplyChunkEntityAnchoring(entity, meta.EntityPrototype.ID, anchored);

            ent.Comp.GeneratedEntities.Add(entity);
        }

        if (!ent.Comp.GeneratedEntities.Contains(spawned) && Exists(spawned))
        {
            var spawnedMeta = MetaData(spawned);
            if (spawnedMeta.EntityPrototype != null)
            {
                if (IsTransientChunkSpawnerPrototype(spawnedMeta.EntityPrototype.ID))
                {
                    QueueDel(spawned);
                    return;
                }

                ApplyChunkEntityAnchoring(spawned, spawnedMeta.EntityPrototype.ID, anchored);
            }

            ent.Comp.GeneratedEntities.Add(spawned);
        }
    }

    private HashSet<EntityUid> GetTileEntities(EntityUid gridUid, MapGridComponent grid, Vector2i indices)
    {
        var tileRef = _mapSystem.GetTileRef(gridUid, grid, indices);
        return _lookup.GetLocalEntitiesIntersecting(tileRef, flags: LookupFlags.Dynamic | LookupFlags.Static | LookupFlags.StaticSundries | LookupFlags.Sundries | LookupFlags.Approximate).ToHashSet();
    }

    private void ApplyChunkEntityAnchoring(EntityUid entity, string prototypeId, bool? anchored)
    {
        var xform = Transform(entity);
        var shouldAnchor = anchored ?? ChunkEntityMutationRules.ShouldAnchor(prototypeId);

        if (shouldAnchor)
        {
            if (!xform.Anchored)
                _transform.AnchorEntity(entity, xform);

            return;
        }

        if (xform.Anchored)
            _transform.Unanchor(entity, xform);
    }

    private static bool IsTransientChunkSpawnerPrototype(string prototypeId)
    {
        return prototypeId.Contains("Mineral", StringComparison.OrdinalIgnoreCase)
            || prototypeId.EndsWith("RoomMarker", StringComparison.OrdinalIgnoreCase)
            || prototypeId.EndsWith("Spawner", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChunkMaterialPrototype(string prototypeId)
    {
        return prototypeId.StartsWith("Wall", StringComparison.OrdinalIgnoreCase)
            || prototypeId.StartsWith("NFWall", StringComparison.OrdinalIgnoreCase)
            || prototypeId.EndsWith("RoomMarker", StringComparison.OrdinalIgnoreCase)
            || prototypeId.Contains("Mineral", StringComparison.OrdinalIgnoreCase)
            || prototypeId.EndsWith("Spawner", StringComparison.OrdinalIgnoreCase)
            || string.Equals(prototypeId, "Grille", StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, EntitySpawnCollectionCache>? GetBiomeCache(SectorAsteroidBiomePrototype? biome)
    {
        if (biome == null)
            return null;

        if (_biomeCaches.TryGetValue(biome.ID, out var cache))
            return cache;

        cache = biome.Entries.ToDictionary(pair => pair.Key, pair => new EntitySpawnCollectionCache(pair.Value));
        _biomeCaches[biome.ID] = cache;
        return cache;
    }

    private List<Entity<MapGridComponent>> GetBlockingGrids(EntityUid sectorMap, EntityUid sectorGridUid, WorldChunkComponent chunk)
    {
        var results = new List<Entity<MapGridComponent>>();

        if (!TryComp<MapComponent>(sectorMap, out var mapComp))
            return results;

        var chunkOrigin = chunk.Coordinates * WorldGen.ChunkSize;
        var worldBounds = Box2.FromDimensions(chunkOrigin, new Vector2(WorldGen.ChunkSize, WorldGen.ChunkSize));
        _mapManager.FindGridsIntersecting(mapComp.MapId, worldBounds, ref results, includeMap: false);
        results.RemoveAll(grid => grid.Owner == sectorGridUid);
        return results;
    }

    private bool IsBlockedByOtherGrid(Vector2 worldPos, MapId mapId, List<Entity<MapGridComponent>> blockedGrids)
    {
        if (blockedGrids.Count == 0)
            return false;

        var coords = new MapCoordinates(worldPos, mapId);
        return blockedGrids.Any(grid => !_mapSystem.GetTileRef(grid.Owner, grid.Comp, coords).Tile.IsEmpty);
    }

    private bool TryGetPlanetWallPrototype(EntityUid gridUid, MapGridComponent grid, Vector2i indices, out string prototype)
    {
        prototype = string.Empty;

        var tile = _mapSystem.GetTileRef(gridUid, grid, indices);
        if (tile.Tile.IsEmpty)
            return false;

        var tileId = tile.Tile.GetContentTileDefinition(_tileDefs).ID;
        var baseWall = GetBaseWallPrototype(tileId);
        if (baseWall == null)
            return false;

        var hash = HashCode.Combine(tileId, indices.X, indices.Y);
        if ((hash & 0xF) < 12)
        {
            prototype = baseWall;
            return _proto.HasIndex<EntityPrototype>(prototype);
        }

        var suffix = OreSuffixes[Math.Abs(hash) % OreSuffixes.Length];
        var oreWall = $"{baseWall}{suffix}";
        if (_proto.HasIndex<EntityPrototype>(oreWall))
        {
            prototype = oreWall;
            return true;
        }

        prototype = baseWall;
        return _proto.HasIndex<EntityPrototype>(prototype);
    }

    private static string? GetBaseWallPrototype(string tileId)
    {
        if (tileId.Contains("Basalt", StringComparison.OrdinalIgnoreCase))
            return "NFWallBasaltCobblebrick";

        if (tileId.Contains("Chromite", StringComparison.OrdinalIgnoreCase))
            return "NFWallChromiteCobblebrick";

        if (tileId.Contains("Andesite", StringComparison.OrdinalIgnoreCase) || tileId.Contains("Drought", StringComparison.OrdinalIgnoreCase))
            return "NFWallAndesiteCobblebrick";

        if (tileId.Contains("Snow", StringComparison.OrdinalIgnoreCase))
            return "NFWallSnowCobblebrick";

        if (tileId.Contains("Ice", StringComparison.OrdinalIgnoreCase))
            return "NFWallIce";

        if (tileId.Contains("Sand", StringComparison.OrdinalIgnoreCase))
            return "NFWallSandCobblebrick";

        if (tileId.Contains("Asteroid", StringComparison.OrdinalIgnoreCase))
            return "NFWallAsteroidCobblebrick";

        return "NFWallCobblebrick";
    }
}