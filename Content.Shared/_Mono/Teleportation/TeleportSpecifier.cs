/* using Robust.Shared.Audio;

// VRS port: Mono — TeleportSpecifier configuration block consumed by Mono's
// ScramActionSystem (and any future RandomTeleport caller that wants a richer
// configuration than the legacy radius+sound+attempts triple).
namespace Content.Shared.Teleportation;

[DataDefinition]
public sealed partial class TeleportSpecifier
{
    [DataField]
    public float TeleportRadius = 100f;

    /// <summary>
    /// Minimum teleport radius to choose, as fraction of maximum radius.
    /// </summary>
    [DataField]
    public float MinRadiusFraction = 0f;

    [DataField]
    public int TeleportAttempts = 20;

    [DataField]
    public SoundSpecifier TeleportSound = new SoundPathSpecifier("/Audio/Effects/teleport_arrival.ogg");

    [DataField]
    public bool AvoidSpace = true;

    /// <summary>
    /// Try teleport closer if repeatedly trying to teleport and finding space.
    /// </summary>
    [DataField]
    public bool ForceSafe = true;
} */
