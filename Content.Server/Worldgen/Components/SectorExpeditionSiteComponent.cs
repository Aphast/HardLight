using System.Numerics;

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
}