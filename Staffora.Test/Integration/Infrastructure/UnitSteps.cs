using Staffora.Core.Models;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Services;

namespace Staffora.Test.Integration.Infrastructure;

public static class UnitSteps
{
    public static OrganizationUnit Draft(string name, OrganizationUnitType type, Guid? parentId, string? description = null) =>
        new() { Name = name, UnitType = type, ParentId = parentId, Description = description, State = OrganizationUnitState.Create };

    public static Task<Result<Guid>> CreateAsync(this OrganizationService service, OrganizationUnit unit, CancellationToken cancellationToken) =>
        service.CreateOrganizationUnitAsync(unit, OrganizationUnitWorkflowAttribute.CreateStep, cancellationToken);

    public static async Task<Guid> CreateUnitIdAsync(this OrganizationService service, string name, OrganizationUnitType type, Guid parentId, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(Draft(name, type, parentId), cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    public static async Task<Result> EditAsync(this OrganizationService service, Guid unitId, Action<OrganizationUnit> edit, CancellationToken cancellationToken)
    {
        var loaded = await service.LoadOrganizationUnitUpdaterAsync(unitId, cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return Result.Failure(loaded.Error);
        }

        edit(loaded.Value.Entity);
        return await service.UpdateOrganizationUnitAndTransitionAsync(loaded.Value, OrganizationUnitWorkflowAttribute.SaveStep, null, cancellationToken);
    }

    public static async Task<Result<OrganizationUnit>> CreateUnitAsync(this OrganizationService service, OrganizationUnit unit)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var created = await service.CreateAsync(unit, cancellationToken);
        if (!created.IsSuccess)
        {
            return Result<OrganizationUnit>.Failure(created.Error);
        }

        return await service.LoadUnitAsync(created.Value);
    }

    public static async Task<Result<OrganizationUnit>> LoadUnitAsync(this OrganizationService service, Guid unitId)
    {
        var loaded = await service.LoadOrganizationUnitUpdaterAsync(unitId, TestContext.Current.CancellationToken);
        return loaded.IsSuccess && loaded.Value is { } updater ? Result<OrganizationUnit>.Success(updater.Entity) : Result<OrganizationUnit>.Failure(loaded.Error);
    }

    public static async Task<Result<OrganizationUnit>> UpdateUnitAsync(this OrganizationService service, OrganizationUnit edited)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var saved = await service.EditAsync(edited.Id, unit =>
        {
            unit.Name = edited.Name;
            unit.Description = edited.Description;
            unit.UnitType = edited.UnitType;
            unit.ParentId = edited.ParentId;
            unit.Parent = null;
            foreach (var value in unit.Attributes.Where(value => edited.Attributes.All(other => other.EntityAttributeDefinitionId != value.EntityAttributeDefinitionId)).ToList())
            {
                unit.Attributes.Remove(value);
            }

            foreach (var value in edited.Attributes)
            {
                var row = unit.Attributes.FirstOrDefault(held => held.EntityAttributeDefinitionId == value.EntityAttributeDefinitionId);
                if (row is null)
                {
                    row = new OrganizationUnitAttribute { EntityAttributeDefinitionId = value.EntityAttributeDefinitionId };
                    unit.Attributes.Add(row);
                }

                AttributeValues.CopyValues(value, row);
            }
        }, cancellationToken);
        return saved.IsSuccess ? await service.LoadUnitAsync(edited.Id) : Result<OrganizationUnit>.Failure(saved.Error);
    }

    public static async Task<Result> DeleteAsync(this OrganizationService service, Guid unitId, CancellationToken cancellationToken)
    {
        var loaded = await service.LoadOrganizationUnitUpdaterAsync(unitId, cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return Result.Failure(loaded.Error);
        }

        return await service.UpdateOrganizationUnitAndTransitionAsync(loaded.Value, DeleteStepOf(loaded.Value.Entity.State), null, cancellationToken);
    }

    public static string DeleteStepOf(OrganizationUnitState state) => state switch
    {
        OrganizationUnitState.Inactive => OrganizationUnitWorkflowAttribute.DeleteInactiveStep,
        OrganizationUnitState.Archived => OrganizationUnitWorkflowAttribute.DeleteArchivedStep,
        _ => OrganizationUnitWorkflowAttribute.DeleteStep,
    };

    public static async Task<Result> StepAsync(this OrganizationService service, Guid unitId, string transitionKey, CancellationToken cancellationToken)
    {
        var loaded = await service.LoadOrganizationUnitUpdaterAsync(unitId, cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return Result.Failure(loaded.Error);
        }

        return await service.UpdateOrganizationUnitAndTransitionAsync(loaded.Value, transitionKey, null, cancellationToken);
    }
}
