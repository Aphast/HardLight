namespace Content.Server.Worldgen.Components;

/// <summary>
/// Stores the root seed for a streamed world map so chunk noise can be reproduced deterministically.
/// </summary>
[RegisterComponent]
public sealed partial class WorldSeedComponent : Component
{
    /// <summary>
    /// Root seed for this world map. A value of 0 means it has not been initialized yet.
    /// </summary>
    [DataField]
    public int Seed;
}