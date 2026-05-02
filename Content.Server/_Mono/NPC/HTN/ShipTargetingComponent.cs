using Robust.Shared.Map;
using System.Numerics;

namespace Content.Server._Mono.NPC.HTN;

/// <summary>
/// Added to entities that are controlling their ship parent to fire guns.
/// </summary>
[RegisterComponent]
public sealed partial class ShipTargetingComponent : Component
{
    /// <summary>
    /// Coordinates we're targeting.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public EntityCoordinates Target;

    /// <summary>
    /// How good to lead the target.
    /// </summary>
    [DataField]
    public float LeadingAccuracy = 1f;

    /// <summary>
    /// Base hitscan aim dispersion in meters (normalized by HitscanDispersionNormalizationDistance).
    /// Zero disables dispersion.
    /// </summary>
    [DataField]
    public float HitscanDispersion = 0f;

    /// <summary>
    /// Distance normalization factor for hitscan dispersion scaling.
    /// Effective spread grows with distance but asymptotically flattens.
    /// </summary>
    [DataField]
    public float HitscanDispersionNormalizationDistance = 250f;

    /// <summary>
    /// Whether hitscan weapons should fire in bursts with short randomized re-aim pauses.
    /// </summary>
    [DataField]
    public bool HitscanBurstEnabled = false;

    /// <summary>
    /// Minimum duration of a continuous hitscan burst in seconds.
    /// </summary>
    [DataField]
    public float HitscanBurstMinDuration = 0.35f;

    /// <summary>
    /// Maximum duration of a continuous hitscan burst in seconds.
    /// </summary>
    [DataField]
    public float HitscanBurstMaxDuration = 0.9f;

    /// <summary>
    /// Minimum delay between bursts to simulate short re-aiming pauses.
    /// </summary>
    [DataField]
    public float HitscanReaimMinDelay = 0.12f;

    /// <summary>
    /// Maximum delay between bursts to simulate short re-aiming pauses.
    /// </summary>
    [DataField]
    public float HitscanReaimMaxDelay = 0.3f;

    /// <summary>
    /// Velocity we're currently estimating for imperfect target leading.
    /// </summary>
    [DataField]
    public Vector2 CurrentLeadingVelocity = Vector2.Zero;

    /// <summary>
    /// Cached list of cannons we'll try to fire.
    /// </summary>
    [DataField]
    public List<EntityUid> Cannons = new();

    /// <summary>
    /// Accumulator of checking the grid's weapons.
    /// </summary>
    [ViewVariables]
    public float WeaponCheckAccum = 0f;

    /// <summary>
    /// How often to re-check available weapons.
    /// </summary>
    [ViewVariables]
    public float WeaponCheckSpacing = 3f;

    [ViewVariables]
    public float HitscanBurstTimeRemaining = 0f;

    [ViewVariables]
    public float HitscanReaimTimeRemaining = 0f;
}
