using System.Numerics;

namespace Content.Server.Worldgen.Components;

/// <summary>
/// Carves solid asteroid mass into the shared sector grid for a streamed chunk.
/// </summary>
[RegisterComponent]
public sealed partial class SectorChunkCarverComponent : Component
{
    [DataField]
    public string DensityNoiseChannel = "Density";

    [DataField]
    public string CarveNoiseChannel = "Carver";

    [DataField]
    public string IslandNoiseChannel = "Wreck";

    [DataField]
    public float SparseFieldScale = 104f;

    [DataField]
    public float IslandFieldScale = 22f;

    [DataField]
    public float DetailFieldScale = 9f;

    [DataField]
    public float SparseThreshold = 0.978f;

    [DataField]
    public float DensityThreshold = 0.79f;

    [DataField]
    public Vector2 CarveRange = new(0.46f, 0.54f);

    [DataField]
    public float IslandThreshold = 0.9f;

    [DataField]
    public float DensitySharpness = 2.35f;

    [DataField]
    public float PlanetFalloff = 0.08f;

    [ViewVariables]
    public HashSet<Vector2i> GeneratedTiles = new();

    [ViewVariables]
    public HashSet<EntityUid> GeneratedEntities = new();
}