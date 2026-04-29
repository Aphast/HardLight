using Robust.Shared.Containers;
using Robust.Shared.GameStates;

namespace Content.Shared.StatusEffectNew.Components;

[RegisterComponent, NetworkedComponent]
public sealed partial class StatusEffectContainerComponent : Component
{
    public const string ContainerId = "status-effects";

    [ViewVariables]
    public Container? ActiveStatusEffects;
}
