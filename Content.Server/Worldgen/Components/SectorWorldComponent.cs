using System.Numerics;
using Content.Shared.Parallax.Biomes;

namespace Content.Server.Worldgen.Components;

/// <summary>
/// Authoritative runtime state for a streamed sector map.
/// </summary>
[RegisterComponent]
public sealed partial class SectorWorldComponent : Component
{
    [DataField]
    public int UniverseSeed;

    [DataField]
    public List<SectorPlanetTypeDefinition> PlanetTypes = new();

    [DataField]
    public float MissionReservationRadius = 196f;

    [DataField]
    public float MissionReservationPadding = 128f;

    [ViewVariables]
    public EntityUid? SectorGrid;

    [ViewVariables]
    public EntityUid? SpaceMap;

    [ViewVariables]
    public EntityUid? FtlMap;

    [ViewVariables]
    public EntityUid? ColCommMap;

    [ViewVariables]
    public Dictionary<string, EntityUid> PlanetTypeMaps = new();

    [ViewVariables]
    public List<SectorPlanetDescriptor> Planets = new();

    [ViewVariables]
    public Dictionary<EntityUid, SectorExpeditionReservation> Reservations = new();

    [ViewVariables]
    public List<EntityUid> StartupLoaders = new();
}

[DataDefinition]
public sealed partial class SectorPlanetTypeDefinition
{
    [DataField(required: true)]
    public string Id = string.Empty;

    [DataField(required: true)]
    public string Name = string.Empty;

    [DataField(required: true)]
    public string BiomeTemplate = string.Empty;

    [DataField(required: true)]
    public List<string> SurfaceTiles = new();

    [DataField]
    public float MinRadius = 900f;

    [DataField]
    public float MaxRadius = 1400f;

    [DataField]
    public float MinTemperature = 240f;

    [DataField]
    public float MaxTemperature = 360f;

    [DataField]
    public float MinOxygen = 0f;

    [DataField]
    public float MaxOxygen = 24f;

    [DataField]
    public float MinNitrogen = 0f;

    [DataField]
    public float MaxNitrogen = 80f;

    [DataField]
    public float MinCarbonDioxide = 0f;

    [DataField]
    public float MaxCarbonDioxide = 8f;

    [DataField]
    public string? WeatherPrototype;
}

[DataDefinition]
public sealed partial class SectorPlanetDescriptor
{
    [DataField]
    public string PlanetId = string.Empty;

    [DataField]
    public string Name = string.Empty;

    [DataField]
    public string PlanetTypeId = string.Empty;

    [DataField]
    public string BiomeTemplate = string.Empty;

    [DataField]
    public string SurfaceTile = "FloorSteel";

    [DataField]
    public Vector2 Center;

    [DataField]
    public float Radius;

    [DataField]
    public int Seed;

    [DataField]
    public float Temperature;

    [DataField]
    public float Oxygen;

    [DataField]
    public float Nitrogen;

    [DataField]
    public float CarbonDioxide;

    [DataField]
    public string TimeOfDay = "Dawn";

    [DataField]
    public string? WeatherPrototype;
}

[DataDefinition]
public sealed partial class SectorExpeditionReservation
{
    [DataField]
    public EntityUid ExpeditionUid;

    [DataField]
    public string PlanetId = string.Empty;

    [DataField]
    public Vector2 Center;

    [DataField]
    public float Radius;
}

[DataDefinition]
public sealed partial class SectorExpeditionPlacement
{
    [DataField]
    public EntityUid SectorMap;

    [DataField]
    public string PlanetTypeId = string.Empty;

    [DataField]
    public Vector2 Center;

    [DataField]
    public float ReservationRadius;

    [DataField]
    public SectorPlanetDescriptor Planet = new();
}