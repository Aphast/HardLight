using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;
using Content.Shared.Power;

namespace Content.Server.Power.Components
{
    [RegisterComponent]
    public sealed partial class CablePlacerComponent : Component
    {
        [DataField("cablePrototypeID", customTypeSerializer:typeof(PrototypeIdSerializer<EntityPrototype>))]
        public string? CablePrototypeId = "CableHV";

        [DataField("blockingWireType")]
        public CableType BlockingCableType = CableType.HighVoltage;

        /// <summary>
        /// Whether the placed cable should go over tiles or not.
        /// </summary>
        [DataField]
        public bool OverTile;
    }
}
