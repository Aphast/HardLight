using Content.Shared.Damage.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared.Damage.Components;

/// <summary>
/// Multiplies the entity's <see cref="StaminaComponent.StaminaDamage"/> by the <see cref="Modifier"/>.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, Access(typeof(StaminaSystem))]
public sealed partial class StaminaModifierComponent : Component
{
    [ViewVariables(VVAccess.ReadWrite), DataField("modifier"), AutoNetworkedField]
    public float Modifier = 2f;
}
