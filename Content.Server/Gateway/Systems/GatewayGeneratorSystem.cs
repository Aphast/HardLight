using System.Linq;
using System.Threading.Tasks;
using Content.Server.Gateway.Components;
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Server.Shuttles.Components;
using Content.Server.Worldgen;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Systems;
using Content.Shared.CCVar;
using Content.Shared.Dataset;
using Content.Shared.Gateway.Components;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Procedural;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.GameObjects;

namespace Content.Server.Gateway.Systems;

/// <summary>
/// Generates gateway destinations regularly and indefinitely that can be chosen from.
/// </summary>
public sealed class GatewayGeneratorSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfgManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!;
    [Dependency] private readonly BiomeSystem _biome = default!;
    [Dependency] private readonly DungeonSystem _dungeon = default!;
    [Dependency] private readonly GatewaySystem _gateway = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly TileSystem _tile = default!;
    [Dependency] private readonly SectorWorldSystem _sectorWorld = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly WorldControllerSystem _worldController = default!;

    private static readonly ProtoId<DungeonConfigPrototype> ExperimentDungeonId = "Experiment";

    // TODO:
    // Fix shader some more
    // Show these in UI
    // Use regular mobs for thingo.

    // Use salvage mission params
    // Add the funny song
    // Put salvage params in the UI

    // Re-use salvage config stuff for the RNG
    // Have it in the UI like expeditions.

    // Also add weather coz it's funny.

    // Add songs (incl. the downloaded one) to the ambient music playlist for planet probably.
    // Copy most of salvage mission spawner

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GatewayGeneratorComponent, MapInitEvent>(OnGeneratorMapInit);
        SubscribeLocalEvent<GatewayGeneratorComponent, ComponentShutdown>(OnGeneratorShutdown);
        SubscribeLocalEvent<GatewayGeneratorDestinationComponent, AttemptGatewayOpenEvent>(OnGeneratorAttemptOpen);
        SubscribeLocalEvent<GatewayGeneratorDestinationComponent, GatewayOpenEvent>(OnGeneratorOpen);
    }

    private void OnGeneratorShutdown(EntityUid uid, GatewayGeneratorComponent component, ComponentShutdown args)
    {
        foreach (var genUid in component.Generated)
        {
            if (Deleted(genUid))
                continue;

            QueueDel(genUid);
        }
    }

    private void OnGeneratorMapInit(EntityUid uid, GatewayGeneratorComponent generator, MapInitEvent args)
    {
        if (!_cfgManager.GetCVar(CCVars.GatewayGeneratorEnabled))
            return;

        generator.NextUnlock = TimeSpan.FromMinutes(5);

        for (var i = 0; i < 3; i++)
        {
            GenerateDestination(uid, generator);
        }
    }

    private void GenerateDestination(EntityUid uid, GatewayGeneratorComponent? generator = null)
    {
        if (!Resolve(uid, ref generator))
            return;

        if (generator == null)
            return;

        var generatorComp = generator;

        var seed = _random.Next();
        var random = new Random(seed);

        if (!_sectorWorld.TryGetDefaultSectorMap(out _, out var sector) || sector.Planets.Count == 0)
            return;

        var availablePlanets = sector.Planets
            .Where(planet => _sectorWorld.TryGetPersistentMap(planet.PlanetTypeId, out _, out _, sector))
            .ToList();

        if (availablePlanets.Count == 0)
            return;

        var hostPlanet = availablePlanets[random.Next(availablePlanets.Count)];
        if (!_sectorWorld.TryGetPersistentMap(hostPlanet.PlanetTypeId, out var hostMapUid, out _))
            return;

        var hostGridUid = hostMapUid;
        var grid = EnsureComp<MapGridComponent>(hostGridUid);
        var gatewayUid = EntityManager.SpawnEntity(generatorComp.Proto, MapCoordinates.Nullspace);

        if (!_sectorWorld.TryReserveExpeditionSite(seed, gatewayUid, hostPlanet.PlanetTypeId, out var placement))
        {
            QueueDel(gatewayUid);
            return;
        }

        var gatewayName = placement.Planet.Name;
        _metadata.SetEntityName(gatewayUid, gatewayName);
        _xform.SetCoordinates(gatewayUid, new EntityCoordinates(hostGridUid, placement.Center));

        var site = EnsureComp<SectorExpeditionSiteComponent>(gatewayUid);
        site.SectorMap = placement.SectorMap;
        site.PlanetId = placement.Planet.PlanetId;
        site.Center = placement.Center;
        site.Radius = placement.ReservationRadius;

        EnsureComp<WorldLoaderComponent>(gatewayUid);
        _worldController.SetLoaderEnabled(gatewayUid, false);

        var origin = placement.Center.Floored();

        var genDest = AddComp<GatewayGeneratorDestinationComponent>(gatewayUid);
        genDest.Origin = origin;
        genDest.Seed = seed;
        genDest.Generator = uid;

        var gatewayComp = Comp<GatewayComponent>(gatewayUid);
        _gateway.SetDestinationName(gatewayUid, FormattedMessage.FromMarkupOrThrow($"[color=#D381C996]{gatewayName}[/color]"), gatewayComp);
        _gateway.SetEnabled(gatewayUid, true, gatewayComp);
        generatorComp.Generated.Add(gatewayUid);
    }

    private void OnGeneratorAttemptOpen(Entity<GatewayGeneratorDestinationComponent> ent, ref AttemptGatewayOpenEvent args)
    {
        if (ent.Comp.Loaded || args.Cancelled)
            return;

        if (!TryComp(ent.Comp.Generator, out GatewayGeneratorComponent? generatorComp))
            return;

        if (generatorComp.NextUnlock + _metadata.GetPauseTime(ent.Owner) <= _timing.CurTime)
            return;

        args.Cancelled = true;
    }

    private void OnGeneratorOpen(Entity<GatewayGeneratorDestinationComponent> ent, ref GatewayOpenEvent args)
    {
        if (ent.Comp.Loaded)
            return;

        if (TryComp(ent.Comp.Generator, out GatewayGeneratorComponent? generatorComp))
        {
            generatorComp.NextUnlock = _timing.CurTime + generatorComp.UnlockCooldown;
            _gateway.UpdateAllGateways();
            // Generate another destination to keep them going.
            GenerateDestination(ent.Comp.Generator);
        }

        var xform = Transform(ent.Owner);
        var gridUid = xform.GridUid ?? xform.MapUid;
        if (gridUid is not { } resolvedGridUid || !TryComp(resolvedGridUid, out MapGridComponent? grid))
            return;

        if (TryComp<SectorExpeditionSiteComponent>(ent.Owner, out var siteComp))
        {
            PrepareDestinationSite(ent, (ent.Owner, siteComp), xform.MapUid ?? resolvedGridUid, resolvedGridUid, grid);
            var loadRadius = (int) MathF.Ceiling((siteComp.ContentRadius > 0f ? siteComp.ContentRadius : siteComp.Radius) + WorldGen.ChunkSize);
            _worldController.EnsureChunksLoaded(xform.MapUid ?? resolvedGridUid, siteComp.Center, loadRadius, ent.Owner);
        }

        ent.Comp.Locked = false;
        ent.Comp.Loaded = true;

        // Do dungeon
        var seed = ent.Comp.Seed;
        var origin = ent.Comp.Origin;
        var dungeonPosition = origin;
        _ = FinishGeneratorOpenAsync(ent, resolvedGridUid, grid, xform.MapUid ?? resolvedGridUid, dungeonPosition, seed, generatorComp);
    }

    private async Task FinishGeneratorOpenAsync(
        Entity<GatewayGeneratorDestinationComponent> ent,
        EntityUid gridUid,
        MapGridComponent grid,
        EntityUid hostMapUid,
        Vector2i dungeonPosition,
        int seed,
        GatewayGeneratorComponent? generatorComp)
    {
        var random = new Random(seed);

        await _dungeon.GenerateDungeonAsync(_protoManager.Index(ExperimentDungeonId), "Experiment", gridUid, grid, dungeonPosition, seed);

        if (TryComp<SectorExpeditionSiteComponent>(ent.Owner, out var siteComp))
            _sectorWorld.CaptureHostedSiteGeneratedEntities((ent.Owner, siteComp), hostMapUid, siteComp.Center, siteComp.ContentRadius > 0f ? siteComp.ContentRadius : siteComp.Radius);

        // TODO: Dungeon mobs + loot.

        // Do markers on the map.
        if (TryComp(gridUid, out BiomeComponent? biomeComp) && generatorComp != null)
        {
            var lootLayers = generatorComp.LootLayers.ToList();

            for (var i = 0; i < generatorComp.LootLayerCount; i++)
            {
                var layerIdx = random.Next(lootLayers.Count);
                var layer = lootLayers[layerIdx];
                lootLayers.RemoveSwap(layerIdx);

                _biome.AddMarkerLayer(gridUid, biomeComp, layer.Id);
            }

            var mobLayers = generatorComp.MobLayers.ToList();

            for (var i = 0; i < generatorComp.MobLayerCount; i++)
            {
                var layerIdx = random.Next(mobLayers.Count);
                var layer = mobLayers[layerIdx];
                mobLayers.RemoveSwap(layerIdx);

                _biome.AddMarkerLayer(gridUid, biomeComp, layer.Id);
            }

            if (TryComp<SectorExpeditionSiteComponent>(ent.Owner, out siteComp))
                _sectorWorld.CaptureHostedSiteGeneratedEntities((ent.Owner, siteComp), hostMapUid, siteComp.Center, siteComp.ContentRadius > 0f ? siteComp.ContentRadius : siteComp.Radius);
        }
    }

    private void PrepareDestinationSite(
        Entity<GatewayGeneratorDestinationComponent> ent,
        Entity<SectorExpeditionSiteComponent> site,
        EntityUid hostMapUid,
        EntityUid hostGridUid,
        MapGridComponent grid)
    {
        if (site.Comp.HostGridUid != EntityUid.Invalid)
            return;

        var captureRadius = site.Comp.Radius + 32f;
        var loadRadius = (int) MathF.Ceiling(captureRadius + WorldGen.ChunkSize);
        _worldController.SetLoaderRadius(ent.Owner, loadRadius);
        _worldController.SetLoaderEnabled(ent.Owner, true);
        _worldController.EnsureChunksLoaded(hostMapUid, site.Comp.Center, loadRadius, ent.Owner);
        _sectorWorld.CaptureHostedSiteBaseline(site, hostGridUid, grid, site.Comp.Center, captureRadius);
        StampLandingPad(hostGridUid, grid, ent.Comp.Origin, ent.Comp.Seed);
    }

    private void StampLandingPad(EntityUid hostGridUid, MapGridComponent grid, Vector2i origin, int seed)
    {
        var tileDef = _tileDefManager["FloorSteel"];
        var random = new Random(seed);
        var tiles = new List<(Vector2i Index, Tile Tile)>(25);

        for (var x = -2; x <= 2; x++)
        {
            for (var y = -2; y <= 2; y++)
            {
                tiles.Add((new Vector2i(x, y) + origin, new Tile(tileDef.TileId, variant: _tile.PickVariant((ContentTileDefinition) tileDef, random))));
            }
        }

        _maps.SetTiles(hostGridUid, grid, tiles);
    }
}
