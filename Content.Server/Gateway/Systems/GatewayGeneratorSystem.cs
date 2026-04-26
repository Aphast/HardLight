using System.Linq;
using Content.Server.Gateway.Components;
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Systems;
using Content.Shared.CCVar;
using Content.Shared.Dataset;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Procedural;
using Content.Shared.Salvage;
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
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!;
    [Dependency] private readonly BiomeSystem _biome = default!;
    [Dependency] private readonly DungeonSystem _dungeon = default!;
    [Dependency] private readonly GatewaySystem _gateway = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly SharedSalvageSystem _salvage = default!;
    [Dependency] private readonly TileSystem _tile = default!;
    [Dependency] private readonly SectorWorldSystem _sectorWorld = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;

    private static readonly ProtoId<LocalizedDatasetPrototype> PlanetNamesId = "NamesBorer";
    private static readonly ProtoId<BiomeTemplatePrototype> ContinentalId = "Continental";
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

        var tileDef = _tileDefManager["FloorSteel"];
        var tiles = new List<(Vector2i Index, Tile Tile)>();
        var seed = _random.Next();
        var random = new Random(seed);

        if (!_sectorWorld.TryGetDefaultSectorMap(out _, out var sector) || sector.Planets.Count == 0)
            return;

        var hostPlanet = sector.Planets[random.Next(sector.Planets.Count)];
        if (!_sectorWorld.TryGetPersistentMap(hostPlanet.PlanetTypeId, out var hostMapUid, out _))
            return;

        var mapId = Comp<MapComponent>(hostMapUid).MapId;
        var siteGrid = _mapManager.CreateGridEntity(mapId);
        var mapUid = siteGrid.Owner;

        if (!_sectorWorld.TryReserveExpeditionSite(seed, mapUid, hostPlanet.PlanetTypeId, out var placement))
        {
            QueueDel(mapUid);
            return;
        }

        var gatewayName = _salvage.GetFTLName(_protoManager.Index(PlanetNamesId), seed);
        _metadata.SetEntityName(mapUid, gatewayName);
        _xform.SetCoordinates(mapUid, new EntityCoordinates(placement.SectorMap, placement.Center));

        var site = EnsureComp<SectorExpeditionSiteComponent>(mapUid);
        site.SectorMap = placement.SectorMap;
        site.PlanetId = placement.Planet.PlanetId;
        site.Center = placement.Center;
        site.Radius = placement.ReservationRadius;

        var biome = EnsureComp<BiomeComponent>(mapUid);
        var biomeTemplate = string.IsNullOrWhiteSpace(hostPlanet.BiomeTemplate)
            ? ContinentalId
            : new ProtoId<BiomeTemplatePrototype>(hostPlanet.BiomeTemplate);
        _biome.SetTemplate(mapUid, biome, _protoManager.Index(biomeTemplate));
        _biome.SetSeed(mapUid, biome, seed);

        var origin = Vector2i.Zero;
        var grid = siteGrid.Comp;

        for (var x = -2; x <= 2; x++)
        {
            for (var y = -2; y <= 2; y++)
            {
                tiles.Add((new Vector2i(x, y) + origin, new Tile(tileDef.TileId, variant: _tile.PickVariant((ContentTileDefinition) tileDef, random))));
            }
        }

        // Clear area nearby as a sort of landing pad.
        _maps.SetTiles(mapUid, grid, tiles);

        _metadata.SetEntityName(mapUid, gatewayName);
        var originCoords = new EntityCoordinates(mapUid, origin);

        var genDest = AddComp<GatewayGeneratorDestinationComponent>(mapUid);
        genDest.Origin = origin;
        genDest.Seed = seed;
        genDest.Generator = uid;

        // Create the gateway.
        var gatewayUid = SpawnAtPosition(generator.Proto, originCoords);
        var gatewayComp = Comp<GatewayComponent>(gatewayUid);
        _gateway.SetDestinationName(gatewayUid, FormattedMessage.FromMarkupOrThrow($"[color=#D381C996]{gatewayName}[/color]"), gatewayComp);
        _gateway.SetEnabled(gatewayUid, true, gatewayComp);
        generator.Generated.Add(mapUid);
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

        if (!TryComp(ent.Owner, out MapGridComponent? grid))
            return;

        ent.Comp.Locked = false;
        ent.Comp.Loaded = true;

        // Do dungeon
        var seed = ent.Comp.Seed;
        var origin = ent.Comp.Origin;
        var random = new Random(seed);
        var dungeonDistance = random.Next(3, 6);
        var dungeonRotation = _dungeon.GetDungeonRotation(seed);
        var dungeonPosition = (origin + dungeonRotation.RotateVec(new Vector2i(0, dungeonDistance))).Floored();

        _dungeon.GenerateDungeon(_protoManager.Index(ExperimentDungeonId), "Experiment", ent.Owner, grid, dungeonPosition, seed); // Frontier: add "Experiment" arg

        // TODO: Dungeon mobs + loot.

        // Do markers on the map.
        if (TryComp(ent.Owner, out BiomeComponent? biomeComp) && generatorComp != null)
        {
            // - Loot
            var lootLayers = generatorComp.LootLayers.ToList();

            for (var i = 0; i < generatorComp.LootLayerCount; i++)
            {
                var layerIdx = random.Next(lootLayers.Count);
                var layer = lootLayers[layerIdx];
                lootLayers.RemoveSwap(layerIdx);

                _biome.AddMarkerLayer(ent.Owner, biomeComp, layer.Id);
            }

            // - Mobs
            var mobLayers = generatorComp.MobLayers.ToList();

            for (var i = 0; i < generatorComp.MobLayerCount; i++)
            {
                var layerIdx = random.Next(mobLayers.Count);
                var layer = mobLayers[layerIdx];
                mobLayers.RemoveSwap(layerIdx);

                _biome.AddMarkerLayer(ent.Owner, biomeComp, layer.Id);
            }
        }
    }
}
