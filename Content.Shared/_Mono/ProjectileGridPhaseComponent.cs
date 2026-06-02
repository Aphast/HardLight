using Robust.Shared.GameStates;

namespace Content.Shared._Mono;

/// <summary>
/// HardLight: marks a projectile that phases through entities on its origin grid (the ship that fired
/// it). Networked so client and server agree on the pass-through, keeping a ship's own shells from
/// colliding with / being slowed by their own hull and shield.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ProjectileGridPhaseComponent : Component
{
    /// <summary>
    /// The grid the projectile was spawned from. Collisions with entities on this grid are ignored.
    /// </summary>
    [ViewVariables]
    public EntityUid? SourceGrid;
}
