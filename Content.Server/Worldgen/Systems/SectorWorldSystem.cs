using System.Numerics;
using System.Linq;
using Content.Server._Mono.Cleanup;
using Content.Server.Atmos.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.Parallax;
using Content.Server.Weather;
using Content.Server.Worldgen.Components;
using Content.Shared.Atmos;
using Content.Shared.Gravity;
using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Shuttles.Components;
using Content.Shared.Weather;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

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
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;
    [Dependency] private readonly WeatherSystem _weather = default!;

    private static readonly string[] TimeOfDayStates = ["Dawn", "Day", "Dusk", "Night"];

    public override void Initialize()
    {
        SubscribeLocalEvent<SectorWorldComponent, ComponentStartup>(OnSectorStartup);
        SubscribeLocalEvent<SectorExpeditionSiteComponent, ComponentShutdown>(OnExpeditionSiteShutdown);
    }

    private void OnSectorStartup(Entity<SectorWorldComponent> ent, ref ComponentStartup args)
    {
        EnsureInitialized(ent);
    }

    private void OnExpeditionSiteShutdown(Entity<SectorExpeditionSiteComponent> ent, ref ComponentShutdown args)
    {
        if (!TryComp(ent.Comp.SectorMap, out SectorWorldComponent? sector))
            return;

        sector.Reservations.Remove(ent.Owner);
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

        if (sector.SectorGrid is not { } resolvedGrid || !Exists(resolvedGrid))
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
        ent.Comp.SpaceMap ??= ent.Owner;

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

        if (ent.Comp.Planets.Count > 0 || ent.Comp.PlanetTypes.Count == 0)
            return;

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
                WeatherPrototype = type.WeatherPrototype,
            });
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
        ent.Comp.FtlMap ??= CreateLayerMap($"{MetaData(ent.Owner).EntityName} FTL", space: true, gravity: false);
        ent.Comp.ColCommMap ??= CreateLayerMap($"{MetaData(ent.Owner).EntityName} ColComm", space: false, gravity: true, mixture: CreateStandardAirMixture(), timeOfDay: "Day");

        EnsurePersistentWorldGrid(ent.Comp.FtlMap.Value);
        EnsurePersistentWorldGrid(ent.Comp.ColCommMap.Value);

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

    private void EnsurePersistentWorldGrid(EntityUid mapOrGridUid)
    {
        if (!Exists(mapOrGridUid))
            return;

        EnsureComp<CleanupImmuneComponent>(mapOrGridUid);

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

        if (!string.IsNullOrWhiteSpace(weatherPrototype) &&
            _proto.TryIndex<WeatherPrototype>(weatherPrototype, out var weather) &&
            TryComp<MapComponent>(mapUid, out var mapComp))
        {
            _weather.SetWeather(mapComp.MapId, weather, null);
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
}