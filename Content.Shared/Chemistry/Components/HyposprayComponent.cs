using Content.Shared.FixedPoint;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Audio;

namespace Content.Shared.Chemistry.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HyposprayComponent : Component
{
    [DataField]
    public string SolutionName = "hypospray";

    [DataField]
    [ViewVariables(VVAccess.ReadWrite)]
    public FixedPoint2 TransferAmount = FixedPoint2.New(5);

    [DataField]
    public SoundSpecifier InjectSound = new SoundPathSpecifier("/Audio/Items/hypospray.ogg");

    /// <summary>
    /// Decides whether you can inject everything or just mobs.
    /// When you can only affect mobs, you're capable of drawing from beakers.
    /// </summary>
    [AutoNetworkedField]
    [DataField(required: true)]
    public bool OnlyAffectsMobs = false;

    /// <summary>
    /// Whether the hypospray is able to draw from containers or if it's a single use
    /// device that can only inject.
    /// </summary>
    [DataField]
    public bool InjectOnly = false;

    #region Non-Instant Hyposprays
    /// <summary>
    /// Whether the hypospray injects its entire capacity on use.
    /// </summary>
    [DataField]
    public bool InjectMaxCapacity = false;

    /// <summary>
    /// The length of the injection do-after.
    /// </summary>
    [DataField]
    public TimeSpan InjectTime = TimeSpan.Zero;

    /// <summary>
    /// Base injection delay for non-instant injections.
    /// </summary>
    [DataField]
    public TimeSpan Delay = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Additional delay applied per unit above the first 5u.
    /// </summary>
    [DataField]
    public TimeSpan DelayPerVolume = TimeSpan.FromSeconds(0.05);

    /// <inheritdoc cref="DoAfter.DoAfterArgs.NeedHand"/>
    [DataField]
    public bool NeedHand = true;

    /// <inheritdoc cref="DoAfter.DoAfterArgs.BreakOnHandChange"/>
    [DataField]
    public bool BreakOnHandChange = true;

    /// <inheritdoc cref="DoAfter.DoAfterArgs.MovementThreshold"/>
    [DataField]
    public float MovementThreshold = 0.1f;
    #endregion

    /// <summary>
    /// Frontier: if true, object will not inject when attacking.
    /// </summary>
    [DataField]
    public bool PreventCombatInjection;

    // Hardlight start: Hardsuit injection prevention

    /// <summary>
    /// Whether this will inject through hardsuits or not.
    /// </summary>
    [DataField]
    public bool PierceArmor = false;

    /// <summary>
    /// Contents of popup message to display to the attacker when injection
    /// fails due to the target wearing a hardsuit.
    /// </summary>
    /// <remarks>
    /// Passed values: $weapon and $target
    /// </remarks>
    [DataField]
    public LocId BlockedByHardsuitPopupMessage = "melee-inject-failed-hardsuit";
}
