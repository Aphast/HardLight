using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.RCD;

[Serializable, NetSerializable]
public sealed class RCDSystemMessage(ProtoId<RCDPrototype> protoId) : BoundUserInterfaceMessage
{
    public ProtoId<RCDPrototype> ProtoId = protoId;
}

[Serializable, NetSerializable]
public sealed class RCDConstructionGhostRotationEvent(NetEntity netEntity, Direction direction) : EntityEventArgs
{
    public readonly NetEntity NetEntity = netEntity;
    public readonly Direction Direction = direction;
}

[Serializable, NetSerializable]
public enum RcdUiKey : byte
{
    Key
}

// Starlight: RPLD
[Serializable, NetSerializable]
public sealed class RPDSelectedLayerEvent : EntityEventArgs
{
    public readonly NetEntity NetEntity;
    public readonly byte Layer;

    public RPDSelectedLayerEvent(NetEntity netEntity, byte layer)
    {
        NetEntity = netEntity;
        Layer = layer;
    }
}