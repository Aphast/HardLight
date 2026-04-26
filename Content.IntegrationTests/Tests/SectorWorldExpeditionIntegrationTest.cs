using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Salvage;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Events;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Systems;
using Content.Shared.Atmos;
using Content.Shared.Gravity;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests;

[TestFixture]
public sealed class SectorWorldExpeditionIntegrationTest
{
    private static List<SectorPlanetTypeDefinition> CreatePlanetTypes() =>
    [
        new SectorPlanetTypeDefinition
        {
            Id = "lava",
            Name = "Lava",
            BiomeTemplate = "NFVGRoidLava",
            SurfaceTiles = ["FloorBasalt"],
            MinTemperature = 700f,
            MaxTemperature = 700f,
            MinOxygen = 0f,
            MaxOxygen = 0f,
            MinNitrogen = 0f,
            MaxNitrogen = 0f,
            MinCarbonDioxide = 18f,
            MaxCarbonDioxide = 18f,
        },
        new SectorPlanetTypeDefinition
        {
            Id = "tundra",
            Name = "Tundra",
            BiomeTemplate = "NFVGRoidSnow",
            SurfaceTiles = ["FloorSnow"],
            MinTemperature = 255f,
            MaxTemperature = 255f,
            MinOxygen = 18f,
            MaxOxygen = 18f,
            MinNitrogen = 60f,
            MaxNitrogen = 60f,
            MinCarbonDioxide = 1f,
            MaxCarbonDioxide = 1f,
        }
    ];

    [Test]
    public async Task PersistentPlanetTypeMapsHaveConfiguredAtmosphereTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        EntityUid sectorMap = default;

        await server.WaitPost(() =>
        {
            var entMan = server.ResolveDependency<IEntityManager>();
            var mapSystem = entMan.System<SharedMapSystem>();
            var sectorWorld = entMan.System<SectorWorldSystem>();
            sectorMap = mapSystem.CreateMap(out _);
            var sector = entMan.EnsureComponent<SectorWorldComponent>(sectorMap);
            sector.UniverseSeed = 1337;
            sector.PlanetTypes = CreatePlanetTypes();
            sectorWorld.TryGetPlanetAtPosition(sectorMap, Vector2.Zero, out _, sector);
        });

        await pair.RunTicksSync(1);

        await server.WaitPost(() =>
        {
            var entMan = server.ResolveDependency<IEntityManager>();
            var atmos = entMan.System<AtmosphereSystem>();
            var sector = entMan.GetComponent<SectorWorldComponent>(sectorMap);

            Assert.That(sector.SpaceMap, Is.EqualTo(sectorMap));
            Assert.That(sector.FtlMap, Is.Not.Null);
            Assert.That(sector.ColCommMap, Is.Not.Null);
            Assert.That(sector.PlanetTypeMaps.Count, Is.EqualTo(sector.PlanetTypes.Count));

            foreach (var planet in sector.Planets)
            {
                Assert.That(sector.PlanetTypeMaps.TryGetValue(planet.PlanetTypeId, out var layerMap), Is.True, planet.PlanetTypeId);
                Assert.That(entMan.TryGetComponent<MapAtmosphereComponent>(layerMap, out var mapAtmos), Is.True, planet.PlanetTypeId);
                Assert.That(entMan.TryGetComponent<GravityComponent>(layerMap, out var gravity), Is.True, planet.PlanetTypeId);
                var mix = atmos.GetTileMixture(null, (layerMap, mapAtmos), Vector2i.Zero);

                Assert.That(gravity.Enabled, Is.True, planet.PlanetTypeId);
                Assert.That(mapAtmos!.Space, Is.False, planet.PlanetTypeId);
                Assert.That(mix, Is.Not.Null, planet.PlanetTypeId);
                Assert.That(mix!.Temperature, Is.EqualTo(planet.Temperature).Within(0.01f), planet.PlanetTypeId);
                Assert.That(mix.GetMoles(Gas.Oxygen), Is.EqualTo(planet.Oxygen).Within(0.01f), planet.PlanetTypeId);
                Assert.That(mix.GetMoles(Gas.Nitrogen), Is.EqualTo(planet.Nitrogen).Within(0.01f), planet.PlanetTypeId);
                Assert.That(mix.GetMoles(Gas.CarbonDioxide), Is.EqualTo(planet.CarbonDioxide).Within(0.01f), planet.PlanetTypeId);
            }

            Assert.That(entMan.TryGetComponent<MapAtmosphereComponent>(sector.FtlMap!.Value, out var ftlAtmos), Is.True);
            Assert.That(ftlAtmos!.Space, Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SharedPlanetMapExpeditionsDoNotBlockOtherConsoleFtlAttemptTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        EntityUid sectorMap = default;

        await server.WaitPost(() =>
        {
            var entMan = server.ResolveDependency<IEntityManager>();
            var mapSystem = entMan.System<SharedMapSystem>();
            var sectorWorld = entMan.System<SectorWorldSystem>();
            sectorMap = mapSystem.CreateMap(out _);
            var sector = entMan.EnsureComponent<SectorWorldComponent>(sectorMap);
            sector.UniverseSeed = 7331;
            sector.PlanetTypes = CreatePlanetTypes();
            sectorWorld.TryGetPlanetAtPosition(sectorMap, Vector2.Zero, out _, sector);
        });

        await pair.RunTicksSync(1);

        await server.WaitPost(() =>
        {
            var entMan = server.ResolveDependency<IEntityManager>();
            var mapMan = server.ResolveDependency<IMapManager>();
            var xform = entMan.System<SharedTransformSystem>();
            var sector = entMan.GetComponent<SectorWorldComponent>(sectorMap);

            var hostPlanet = sector.Planets.First();
            var hostMap = sector.PlanetTypeMaps[hostPlanet.PlanetTypeId];
            var hostMapId = entMan.GetComponent<MapComponent>(hostMap).MapId;

            var expeditionA = mapMan.CreateGridEntity(hostMapId);
            var expeditionB = mapMan.CreateGridEntity(hostMapId);

            xform.SetCoordinates(expeditionA.Owner, new EntityCoordinates(hostMap, Vector2.Zero));
            xform.SetCoordinates(expeditionB.Owner, new EntityCoordinates(hostMap, new Vector2(1024f, 0f)));

            entMan.AddComponent<SalvageExpeditionComponent>(expeditionA.Owner);
            entMan.AddComponent<SalvageExpeditionComponent>(expeditionB.Owner);

            var siteA = entMan.EnsureComponent<SectorExpeditionSiteComponent>(expeditionA.Owner);
            siteA.SectorMap = hostMap;
            siteA.PlanetId = hostPlanet.PlanetId;
            siteA.Center = Vector2.Zero;
            siteA.Radius = 196f;

            var siteB = entMan.EnsureComponent<SectorExpeditionSiteComponent>(expeditionB.Owner);
            siteB.SectorMap = hostMap;
            siteB.PlanetId = hostPlanet.PlanetId;
            siteB.Center = new Vector2(1024f, 0f);
            siteB.Radius = 196f;

            var crew = entMan.SpawnEntity("MobHuman", new EntityCoordinates(expeditionB.Owner, Vector2.Zero));
            Assert.That(entMan.HasComponent<MobStateComponent>(crew), Is.True);

            var ev = new ConsoleFTLAttemptEvent(expeditionA.Owner, false, string.Empty);
            entMan.EventBus.RaiseLocalEvent(expeditionA.Owner, ref ev);

            Assert.That(ev.Cancelled, Is.False, "Crew on a different expedition in the same host map should not block FTL.");
        });

        await pair.CleanReturnAsync();
    }
}