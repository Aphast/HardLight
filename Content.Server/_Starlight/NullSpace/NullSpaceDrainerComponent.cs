namespace Content.Server._Starlight.Shadekin;

[RegisterComponent]
public sealed partial class NullSpaceDrainerComponent : Component
{
    [DataField]
    public EntityUid? Target;

    /// <summary>
    /// Hardlight
    /// If the DrainerComp actully drains Brighteye energy?
    /// </summary>
    [DataField]
    public bool Drains = false;

    /// <summary>
    /// Points drained by energy/sec
    /// </summary>
    [DataField]
    public int Points = 100;
}
