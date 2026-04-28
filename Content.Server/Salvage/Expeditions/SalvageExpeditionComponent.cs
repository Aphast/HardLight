using System.Collections.Generic;
using System.Numerics;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Robust.Shared.Audio;
using Robust.Shared.Maths;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.List;

namespace Content.Server.Salvage.Expeditions;

/// <summary>
/// Designates this entity as holding a salvage expedition.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class SalvageExpeditionComponent : SharedSalvageExpeditionComponent
{
    public SalvageMissionParams MissionParams = default!;

    /// <summary>
    /// Where the dungeon is located for initial announcement.
    /// </summary>
    [DataField("dungeonLocation")]
    public Vector2 DungeonLocation = Vector2.Zero;

    /// <summary>
    /// When the expeditions ends.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("endTime", customTypeSerializer: typeof(TimeOffsetSerializer))]
    [AutoPausedField]
    public TimeSpan? EndTime;

    /// <summary>
    /// Station whose mission this is.
    /// </summary>
    [DataField("station")]
    public EntityUid Station;

    /// <summary>
    /// HardLight: Console that initiated this mission for direct targeting
    /// </summary>
    [DataField("console")]
    public EntityUid? Console;

    [ViewVariables] public bool Completed = false;

    /// <summary>
    /// HardLight: True once expedition return flow has been triggered.
    /// Prevents duplicate FTL/cleanup scheduling.
    /// </summary>
    [ViewVariables]
    public bool ReturnTriggered = false;

    /// <summary>
    /// Persistent grid that physically hosts the expedition content.
    /// </summary>
    [ViewVariables]
    public EntityUid HostGridUid = EntityUid.Invalid;

    /// <summary>
    /// Runtime-only entity snapshot for content stamped onto a persistent host grid.
    /// </summary>
    [ViewVariables]
    public HashSet<EntityUid> GeneratedEntities = new();

    /// <summary>
    /// Runtime-only tile snapshot used to restore the host grid when the expedition is removed.
    /// </summary>
    [ViewVariables]
    public Dictionary<Vector2i, Tile> OriginalTiles = new();

    // Frontier: moved to Client
    /// <summary>
    /// Countdown audio stream.
    /// </summary>
    // [DataField, AutoNetworkedField]
    // public EntityUid? Stream = null;
    // End Frontier: moved to Client

    /// <summary>
    /// Sound that plays when the mission end is imminent.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public SoundSpecifier Sound = new SoundCollectionSpecifier("ExpeditionEnd")
    {
        Params = AudioParams.Default.WithVolume(-5),
    };

    // Frontier: moved to Shared
    /// <summary>
    /// Song selected on MapInit so we can predict the audio countdown properly.
    /// </summary>
    // [DataField]
    // public ResolvedSoundSpecifier SelectedSong;
    // End Frontier: moved to Shared
}
