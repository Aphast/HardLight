namespace Content.Server._HL.Fax;

[RegisterComponent, Access(typeof(BorgHandheldFaxNameSystem))]
public sealed partial class BorgHandheldFaxNameComponent : Component
{
    [DataField]
    public string? DefaultName;
}
