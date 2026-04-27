using Content.Shared.Maps;
using Content.Shared.Storage;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Dictionary;

namespace Content.Shared.Worldgen.Prototypes;

[Prototype("sectorAsteroidBiome")]
public sealed partial class SectorAsteroidBiomePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField("floorTiles", required: true)]
    public List<string> FloorTiles = new();

    [DataField("entries", required: true,
        customTypeSerializer: typeof(PrototypeIdDictionarySerializer<List<EntitySpawnEntry>, ContentTileDefinition>))]
    public Dictionary<string, List<EntitySpawnEntry>> Entries = default!;
}