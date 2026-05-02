using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server.Worldgen.Components;

/// <summary>
/// Marks an expedition grid as occupying a reserved site in the streamed sector.
/// </summary>
[RegisterComponent]
public sealed partial class SectorExpeditionSiteComponent : Component
{
    [DataField]
    public EntityUid SectorMap;

    [DataField]
    public string PlanetId = string.Empty;

    [DataField]
    public Vector2 Center;

    [DataField]
    public float Radius;

    public EntityUid HostGridUid = EntityUid.Invalid;

    public float ContentRadius;

    public Dictionary<Vector2i, Tile> OriginalTiles = new();

    public HashSet<EntityUid> OriginalEntities = new();

    public HashSet<EntityUid> GeneratedEntities = new();

    public Dictionary<Vector2i, Dictionary<Vector2i, Tile>> CachedChunkTiles = new();

    public Dictionary<Vector2i, List<ChunkEntityMutationRecord>> CachedChunkEntities = new();
}