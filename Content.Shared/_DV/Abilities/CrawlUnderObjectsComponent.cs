using Content.Shared.Actions;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using System.Numerics; // HardLight

namespace Content.Shared._DV.Abilities;

/// <summary>
/// Lets an entity toggle sneaking/squeezing under objects at reduced speed. // HardLight: Reworded
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CrawlUnderObjectsComponent : Component
{
    [DataField]
    public EntityUid? ToggleHideAction;

    [DataField] // HardLight: DataField(required: true)<DataField
    public EntProtoId? ActionProto; // HardLight: Added ?

    [DataField, AutoNetworkedField]
    public bool Enabled;

    [DataField]
    public float SneakSpeedModifier = 0.7f;

    // HardLight start
    [DataField]
    public float SqueezeRadiusScale = 1f;

    [DataField]
    public float UnsqueezedRadiusScale = 1f;

    /// <summary>
    /// Circle geometry captured at squeeze start and restored on squeeze end.
    /// Keeps each squeeze cycle independent and prevents cumulative drift.
    /// </summary>
    public List<(string key, Vector2 position, float radius)> ChangedCircles = new();

    /// <summary>
    /// Guards the unsqueezed baseline inflation so it is only applied once.
    /// </summary>
    public bool BaselineInflationApplied;

    /// <summary>
    /// Captured geometry used for temporary downed/laying radius scaling.
    /// </summary>
    public List<(string key, Vector2 position, float radius)> DownedCircles = new();

    /// <summary>
    /// True while downed scaling is currently applied.
    /// </summary>
    public bool DownedScaleApplied;
    // HardLight end
}

[Serializable, NetSerializable]
public enum SneakingVisuals : byte
{
    Sneaking
}

public sealed partial class ToggleCrawlingStateEvent : InstantActionEvent;

[ByRefEvent]
public readonly record struct CrawlingUpdatedEvent(bool Enabled, CrawlUnderObjectsComponent Comp);