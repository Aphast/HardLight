using Robust.Shared.Map;

namespace Content.Server._Mono.NPC.HTN;

/// <summary>
/// Automatically triggers FTL on an AI ship if it gets too close to a player station.
/// Used to keep xenoborg drones from camping station docking areas.
/// </summary>
[RegisterComponent]
public sealed partial class ShipAutoFTLComponent : Component
{
    /// <summary>
    /// Distance from any player station below which this ship will automatically FTL away.
    /// </summary>
    [DataField("ftlTriggerDistance")]
    public float FTLTriggerDistance = 4500f;

    /// <summary>
    /// Distance to FTL to (away from the triggering station).
    /// </summary>
    [DataField("ftlTargetDistance")]
    public float FTLTargetDistance = 10000f;

    /// <summary>
    /// How long to wait between FTL checks (seconds). Prevents spam.
    /// </summary>
    [DataField]
    public float CheckCooldown = 2.0f;

    /// <summary>
    /// Last time an FTL check was performed.
    /// </summary>
    [ViewVariables]
    public TimeSpan LastCheckTime = TimeSpan.Zero;

    /// <summary>
    /// Pending FTL destination if one has been queued.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? PendingFTLCoordinates = null;
}
