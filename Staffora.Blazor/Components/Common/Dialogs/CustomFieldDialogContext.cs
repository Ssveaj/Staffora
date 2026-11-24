using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.CustomFields;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

public abstract class CustomFieldDialogContext<TDefinition> : StateEntityDialogContextBase<CustomFieldState, TDefinition>
    where TDefinition : class, IEntityAttributeDefinition
{
    private bool fieldsLocked = true;

    protected CustomFieldDialogContext(CustomFieldService service, Guid? fieldId, Func<IQueryable<TDefinition>, IQueryable<TDefinition>> graph)
        : base(fieldId, graph)
    {
        Service = service;
    }

    protected CustomFieldService Service { get; }

    public IReadOnlyList<CustomFieldTenant> Tenants { get; private set; } = [];

    public string? Note { get; private set; }

    protected Result Keep(Result<CustomFieldEditorOptions> options)
    {
        if (!options.IsSuccess || options.Value is not { } value)
        {
            return Result.Failure(options.Error);
        }

        this.fieldsLocked = value.FieldsLocked;
        Tenants = value.Tenants;
        Note = value.FieldsLocked ? "The platform sets up custom fields: you can view this field, and fill it in on each record, but not change it." : null;
        return Result.Success();
    }

    protected override bool IsLockedRecord(TDefinition entity) => this.fieldsLocked;
}

[DialogRoute(DialogTokens.UserField)]
public sealed class UserFieldDialogContext : CustomFieldDialogContext<ApplicationUserAttributeDefinition>
{
    public UserFieldDialogContext(CustomFieldService service, Guid? fieldId) : base(service, fieldId, ApplicationUserAttributeDefinitionWorkflowQueries.WithWorkflowGraph)
    {
    }

    protected override Task<Result<DetachedEntityUpdater<ApplicationUserAttributeDefinition>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        Service.LoadApplicationUserAttributeDefinitionUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<ApplicationUserAttributeDefinition> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        Service.UpdateApplicationUserAttributeDefinitionAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Service.GetApplicationUserAttributeDefinitionHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken) =>
        Keep(await Service.GetApplicationUserAttributeDefinitionEditorOptionsAsync(EntityId, cancellationToken));

    protected override Task<Result<ApplicationUserAttributeDefinition>> NewEntityAsync(CancellationToken cancellationToken) =>
        Service.NewApplicationUserAttributeDefinitionAsync(cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(ApplicationUserAttributeDefinition entity, string transitionKey, CancellationToken cancellationToken) =>
        Service.CreateApplicationUserAttributeDefinitionAsync(entity, transitionKey, cancellationToken);
}

[DialogRoute(DialogTokens.UnitField)]
public sealed class UnitFieldDialogContext : CustomFieldDialogContext<OrganizationUnitAttributeDefinition>
{
    public UnitFieldDialogContext(CustomFieldService service, Guid? fieldId) : base(service, fieldId, OrganizationUnitAttributeDefinitionWorkflowQueries.WithWorkflowGraph)
    {
    }

    protected override Task<Result<DetachedEntityUpdater<OrganizationUnitAttributeDefinition>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        Service.LoadOrganizationUnitAttributeDefinitionUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<OrganizationUnitAttributeDefinition> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        Service.UpdateOrganizationUnitAttributeDefinitionAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Service.GetOrganizationUnitAttributeDefinitionHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken) =>
        Keep(await Service.GetOrganizationUnitAttributeDefinitionEditorOptionsAsync(EntityId, cancellationToken));

    protected override Task<Result<OrganizationUnitAttributeDefinition>> NewEntityAsync(CancellationToken cancellationToken) =>
        Service.NewOrganizationUnitAttributeDefinitionAsync(cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(OrganizationUnitAttributeDefinition entity, string transitionKey, CancellationToken cancellationToken) =>
        Service.CreateOrganizationUnitAttributeDefinitionAsync(entity, transitionKey, cancellationToken);
}

[DialogRoute(DialogTokens.JobField)]
public sealed class JobFieldDialogContext : CustomFieldDialogContext<JobAttributeDefinition>
{
    public JobFieldDialogContext(CustomFieldService service, Guid? fieldId) : base(service, fieldId, JobAttributeDefinitionWorkflowQueries.WithWorkflowGraph)
    {
    }

    protected override Task<Result<DetachedEntityUpdater<JobAttributeDefinition>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        Service.LoadJobAttributeDefinitionUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<JobAttributeDefinition> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        Service.UpdateJobAttributeDefinitionAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        Service.GetJobAttributeDefinitionHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken) =>
        Keep(await Service.GetJobAttributeDefinitionEditorOptionsAsync(EntityId, cancellationToken));

    protected override Task<Result<JobAttributeDefinition>> NewEntityAsync(CancellationToken cancellationToken) =>
        Service.NewJobAttributeDefinitionAsync(cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(JobAttributeDefinition entity, string transitionKey, CancellationToken cancellationToken) =>
        Service.CreateJobAttributeDefinitionAsync(entity, transitionKey, cancellationToken);
}
