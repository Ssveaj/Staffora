using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.CustomFields;

namespace Staffora.Test.Integration.Infrastructure;

internal static class CustomFieldSteps
{
    public const string Create = UserFieldWorkflowAttribute.CreateStep;
    public const string SaveDraft = UserFieldWorkflowAttribute.SaveDraftStep;
    public const string Activate = UserFieldWorkflowAttribute.ActivateStep;
    public const string SaveActive = UserFieldWorkflowAttribute.SaveActiveStep;
    public const string Deactivate = UserFieldWorkflowAttribute.DeactivateStep;
    public const string SaveInactive = UserFieldWorkflowAttribute.SaveInactiveStep;
    public const string Reactivate = UserFieldWorkflowAttribute.ReactivateStep;

    public static IEntityAttributeDefinition New(CustomFieldTarget target, string name, AttributeValueType type = AttributeValueType.Text)
    {
        IEntityAttributeDefinition field = target switch
        {
            CustomFieldTarget.OrganizationUnit => new OrganizationUnitAttributeDefinition(),
            CustomFieldTarget.Job => new JobAttributeDefinition(),
            _ => new ApplicationUserAttributeDefinition(),
        };
        field.Name = name;
        field.ValueType = type;
        return field;
    }

    public static async Task<Result<Guid>> CreateFieldAsync(this CustomFieldService service, CustomFieldTarget target, IEntityAttributeDefinition sent, Guid? tenantId = null, bool activate = true, CancellationToken cancellationToken = default)
    {
        sent.TenantId = tenantId;
        var created = sent switch
        {
            ApplicationUserAttributeDefinition user when target == CustomFieldTarget.User => await service.CreateApplicationUserAttributeDefinitionAsync(user, Create, cancellationToken),
            OrganizationUnitAttributeDefinition unit when target == CustomFieldTarget.OrganizationUnit => await service.CreateOrganizationUnitAttributeDefinitionAsync(unit, Create, cancellationToken),
            JobAttributeDefinition job when target == CustomFieldTarget.Job => await service.CreateJobAttributeDefinitionAsync(job, Create, cancellationToken),
            _ => throw new ArgumentException($"{sent.GetType().Name} is no field of {target}.", nameof(sent)),
        };
        if (!created.IsSuccess || !activate)
        {
            return created;
        }

        var activated = await service.StepFieldAsync(target, created.Value, Activate, cancellationToken: cancellationToken);
        return activated.IsSuccess ? created : Result<Guid>.Failure(activated.Error);
    }

    public static async Task<Result<FieldEdit>> LoadFieldAsync(this CustomFieldService service, CustomFieldTarget target, Guid id, CancellationToken cancellationToken = default)
    {
        switch (target)
        {
            case CustomFieldTarget.OrganizationUnit:
                var unit = await service.LoadOrganizationUnitAttributeDefinitionUpdaterAsync(id, cancellationToken);
                return unit.IsSuccess ? Result<FieldEdit>.Success(new FieldEdit(target, unit.Value!.Entity, unit.Value)) : Result<FieldEdit>.Failure(unit.Error);
            case CustomFieldTarget.Job:
                var job = await service.LoadJobAttributeDefinitionUpdaterAsync(id, cancellationToken);
                return job.IsSuccess ? Result<FieldEdit>.Success(new FieldEdit(target, job.Value!.Entity, job.Value)) : Result<FieldEdit>.Failure(job.Error);
            default:
                var user = await service.LoadApplicationUserAttributeDefinitionUpdaterAsync(id, cancellationToken);
                return user.IsSuccess ? Result<FieldEdit>.Success(new FieldEdit(target, user.Value!.Entity, user.Value)) : Result<FieldEdit>.Failure(user.Error);
        }
    }

    public static Task<Result> StepAsync(this CustomFieldService service, FieldEdit edit, string transitionKey, CancellationToken cancellationToken = default) => edit.Updater switch
    {
        DetachedEntityUpdater<ApplicationUserAttributeDefinition> user => service.UpdateApplicationUserAttributeDefinitionAndTransitionAsync(user, transitionKey, null, cancellationToken),
        DetachedEntityUpdater<OrganizationUnitAttributeDefinition> unit => service.UpdateOrganizationUnitAttributeDefinitionAndTransitionAsync(unit, transitionKey, null, cancellationToken),
        DetachedEntityUpdater<JobAttributeDefinition> job => service.UpdateJobAttributeDefinitionAndTransitionAsync(job, transitionKey, null, cancellationToken),
        _ => throw new ArgumentException("The edit holds no field's updater.", nameof(edit)),
    };

    public static async Task<Result> StepFieldAsync(this CustomFieldService service, CustomFieldTarget target, Guid id, string transitionKey, Action<IEntityAttributeDefinition>? change = null, CancellationToken cancellationToken = default)
    {
        var loaded = await service.LoadFieldAsync(target, id, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error);
        }

        change?.Invoke(loaded.Value!.Definition);
        return await service.StepAsync(loaded.Value!, transitionKey, cancellationToken);
    }
}

internal sealed record FieldEdit(CustomFieldTarget Target, IEntityAttributeDefinition Definition, object Updater);
