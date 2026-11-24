namespace Staffora.Core.Workflow.FieldAccess;

[Flags]
public enum FieldAccessFlags
{
    None = 0,
    Visible = 1,
    Editable = 2,
    Mandatory = 4,

    ReadOnly = Visible,

    ReadWrite = Visible | Editable,

    Required = Visible | Editable | Mandatory,
}
