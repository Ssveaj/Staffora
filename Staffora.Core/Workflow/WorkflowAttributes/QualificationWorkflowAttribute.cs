using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class QualificationWorkflowAttribute : WorkflowAttribute
{
    public const string CreateStep = "qualification-create";

    public const string SaveStep = "qualification-save";

    public const string ArchiveStep = "qualification-archive";

    public const string RestoreStep = "qualification-restore";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<QualificationState, Qualification>();

        workflow.AddState(QualificationState.Create).ConfigureFields(f => Editable(f));
        workflow.AddState(QualificationState.Active).ConfigureFields(f => Editable(f));
        workflow.AddState(QualificationState.Archived).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));

        workflow.AddTransition(new(CreateStep, QualificationState.Create, QualificationState.Active)
        {
            Label = "Create",
            Description = "Add the qualification to the catalogue. It can be given to staff members at once",
            Icon = "add_task",
            RequiredPermission = RolePermission.QualificationsManage,
            SortOrder = 30,
        })
        .AddCondition(new RequiredFieldCondition<QualificationState, Qualification>(e => e.Name));

        workflow.AddTransition(new(SaveStep, QualificationState.Active, QualificationState.Active)
        {
            Label = "Save",
            Description = "Save the qualification's name and description. The staff members who hold it keep it",
            Icon = "save",
            RequiredPermission = RolePermission.QualificationsManage,
            SortOrder = 20,
            IsSave = true,
        })
        .AddCondition(new RequiredFieldCondition<QualificationState, Qualification>(e => e.Name));

        workflow.AddTransition(new(ArchiveStep, QualificationState.Active, QualificationState.Archived)
        {
            Label = "Archive",
            Description = "Take the qualification out of the catalogue: it is given to nobody new, and the staff members who hold it keep it on their record",
            Icon = "archive",
            ConfirmationMessage = "Archive this qualification? It is given to nobody new, and the staff members who hold it keep it.",
            RequiredPermission = RolePermission.QualificationsManage,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(RestoreStep, QualificationState.Archived, QualificationState.Active)
        {
            Label = "Restore",
            Description = "Put the qualification back in the catalogue: it can be given to staff members again",
            Icon = "unarchive",
            RequiredPermission = RolePermission.QualificationsManage,
            SortOrder = 30,
        });

        return workflow;
    }

    private static StateFieldBuilder<Qualification> Editable(StateFieldBuilder<Qualification> fields) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.Name, FieldAccessFlags.Required)
        .Field(e => e.Description, FieldAccessFlags.ReadWrite);
}
