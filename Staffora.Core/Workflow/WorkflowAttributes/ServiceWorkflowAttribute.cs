using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class ServiceWorkflowAttribute : WorkflowAttribute
{
    public const string AreaQualifications = "Qualifications";

    public const string CreateStep = "service-create";

    public const string SaveStep = "service-save";

    public const string ArchiveStep = "service-archive";

    public const string RestoreStep = "service-restore";

    public static readonly string CountReason = $"The default number of people is {Service.MinSlotCount} to {Service.MaxSlotCount}";

    public const string QualificationActiveReason = "Each qualification named anew is an active one of the catalogue";

    public const string OwnTenantReason = "Only a user of this organization changes its services";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<ServiceState, Service>();

        workflow.AddState(ServiceState.Create).ConfigureFields(f => Editable(f));
        workflow.AddState(ServiceState.Active).ConfigureFields(f => Editable(f));
        workflow.AddState(ServiceState.Archived).ConfigureFields(f => f
            .Default(FieldAccessFlags.ReadOnly)
            .Area(AreaQualifications, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Qualifications)));

        workflow.AddTransition(new(CreateStep, ServiceState.Create, ServiceState.Active)
        {
            Label = "Create",
            Description = "Add the service to the catalogue. Slots can be made from it at once",
            Icon = "add_business",
            RequiredPermission = RolePermission.ServicesEdit,
            SortOrder = 30,
        })
        .AddCondition(new RequiredFieldCondition<ServiceState, Service>(e => e.Name))
        .AddCondition(CountInRange())
        .AddCondition(QualificationsNamedAnewActive());

        workflow.AddTransition(new(SaveStep, ServiceState.Active, ServiceState.Active)
        {
            Label = "Save",
            Description = "Save the service. Slots made from it before keep what they took",
            Icon = "save",
            RequiredPermission = RolePermission.ServicesEdit,
            SortOrder = 20,
            IsSave = true,
        })
        .AddCondition(new RequiredFieldCondition<ServiceState, Service>(e => e.Name))
        .AddCondition(CountInRange())
        .AddCondition(QualificationsNamedAnewActive());

        workflow.AddTransition(new(ArchiveStep, ServiceState.Active, ServiceState.Archived)
        {
            Label = "Archive",
            Description = "Take the service out of the catalogue: no new slot is made from it, and slots made from it keep what they took",
            Icon = "archive",
            ConfirmationMessage = "Archive this service? No new slot is made from it, and the portal no longer offers it. Jobs that have slots made from it keep them.",
            RequiredPermission = RolePermission.ServicesEdit,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(RestoreStep, ServiceState.Archived, ServiceState.Active)
        {
            Label = "Restore",
            Description = "Put the service back in the catalogue: slots can be made from it again",
            Icon = "unarchive",
            RequiredPermission = RolePermission.ServicesEdit,
            SortOrder = 30,
        })
        .AddCondition(new RequiredFieldCondition<ServiceState, Service>(e => e.Name))
        .AddCondition(CountInRange())
        .AddCondition(QualificationsNamedAnewActive());

        foreach (var transition in workflow.Transitions)
        {
            transition.AddCondition(InOwnTenant());
        }

        return workflow;
    }

    private static StateFieldBuilder<Service> Editable(StateFieldBuilder<Service> fields) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.Name, FieldAccessFlags.Required)
        .Field(e => e.Description, FieldAccessFlags.ReadWrite)
        .Field(e => e.DefaultSlotCount, FieldAccessFlags.ReadWrite)
        .Field(e => e.OfferedInPortal, FieldAccessFlags.ReadWrite)
        .Area(AreaQualifications, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Qualifications));

    private static DelegateCondition<ServiceState, Service> CountInRange() =>
        DelegateCondition<ServiceState, Service>.FromContext(CountReason, context => context.Entity.DefaultSlotCount is >= Service.MinSlotCount and <= Service.MaxSlotCount);

    private static DelegateCondition<ServiceState, Service> QualificationsNamedAnewActive() =>
        DelegateCondition<ServiceState, Service>.FromContext(QualificationActiveReason, context =>
        {
            var kept = context.ServiceProvider?.GetService(typeof(IServiceGuard)) is IServiceGuard guard ? guard.StoredQualifications(context.Entity) : new HashSet<Guid>();
            return context.Entity.Qualifications
                .Where(named => !kept.Contains(named.QualificationId))
                .All(named => named.Qualification is { } qualification && qualification.Id == named.QualificationId && qualification.State == QualificationState.Active);
        }, $"{nameof(Service.Qualifications)}.{nameof(ServiceQualification.Qualification)}");

    private static DelegateCondition<ServiceState, Service> InOwnTenant() =>
        DelegateCondition<ServiceState, Service>.FromContext(OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);
}
