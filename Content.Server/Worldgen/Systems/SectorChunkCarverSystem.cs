using System.Numerics;
using Content.Server.Worldgen.Components;
using Content.Shared.Maps;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

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

    public override void Initialize()
    {
        SubscribeLocalEvent<SectorChunkCarverComponent, WorldChunkLoadedEvent>(OnChunkLoaded);
        SubscribeLocalEvent<SectorChunkCarverComponent, WorldChunkUnloadedEvent>(OnChunkUnloaded);
    }

    private void OnChunkLoaded(Entity<SectorChunkCarverComponent> ent, ref WorldChunkLoadedEvent args)
    {
        if (!TryComp<WorldChunkComponent>(args.Chunk, out var chunk))
            return;

        if (!TryComp<SectorWorldComponent>(chunk.Map, out var sector))
            return;

        if (!_sectorWorld.TryGetSectorGrid(chunk.Map, out var gridUid, sector))
            return;

        var grid = EnsureComp<MapGridComponent>(gridUid);
        var chunkOrigin = chunk.Coordinates * WorldGen.ChunkSize;
        var tiles = new List<(Vector2i, Tile)>(WorldGen.ChunkSize * WorldGen.ChunkSize / 3);

        ent.Comp.GeneratedTiles.Clear();
        ent.Comp.GeneratedEntities.Clear();

        for (var x = 0; x < WorldGen.ChunkSize; x++)
        {
            for (var y = 0; y < WorldGen.ChunkSize; y++)
            {
                var indices = chunkOrigin + new Vector2i(x, y);
                var worldPos = indices + new Vector2(0.5f, 0.5f);

                if (!_sectorWorld.IsSolidAt(chunk.Map, ent.Owner, ent.Comp, worldPos, out var planet))
                    continue;

                if (!_sectorWorld.TryGetSurfaceTile(planet, out var tileId))
                    tileId = "FloorSteel";

                var tileDef = (ContentTileDefinition) _tileDefs[tileId];
                tiles.Add((indices, new Tile(tileDef.TileId)));
                ent.Comp.GeneratedTiles.Add(indices);
            }
        }

        if (tiles.Count > 0)
            _mapSystem.SetTiles(gridUid, grid, tiles);

        foreach (var indices in ent.Comp.GeneratedTiles)
        {
            if (!TryGetPlanetWallPrototype(gridUid, grid, indices, out var wallPrototype))
                continue;

            var spawned = Spawn(wallPrototype, new EntityCoordinates(gridUid, indices + new Vector2(0.5f, 0.5f)));
            ent.Comp.GeneratedEntities.Add(spawned);
        }
    }

    private void OnChunkUnloaded(Entity<SectorChunkCarverComponent> ent, ref WorldChunkUnloadedEvent args)
    {
        if (!TryComp<WorldChunkComponent>(args.Chunk, out var chunk))
            return;

        if (!TryComp<SectorWorldComponent>(chunk.Map, out var sector))
            return;

        if (ent.Comp.GeneratedTiles.Count == 0)
            return;

        foreach (var generated in ent.Comp.GeneratedEntities)
        {
            if (Exists(generated))
                QueueDel(generated);
        }

        ent.Comp.GeneratedEntities.Clear();

        if (!_sectorWorld.TryGetSectorGrid(chunk.Map, out var gridUid, sector))
            return;

        var grid = EnsureComp<MapGridComponent>(gridUid);
        var tiles = new List<(Vector2i, Tile)>(ent.Comp.GeneratedTiles.Count);

        foreach (var indices in ent.Comp.GeneratedTiles)
        {
            tiles.Add((indices, Tile.Empty));
        }

        _mapSystem.SetTiles(gridUid, grid, tiles);
        ent.Comp.GeneratedTiles.Clear();
    }

    private bool TryGetPlanetWallPrototype(EntityUid gridUid, MapGridComponent grid, Vector2i indices, out string prototype)
    {
        prototype = string.Empty;

        if (!grid.TryGetTileRef(indices, out var tile))
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