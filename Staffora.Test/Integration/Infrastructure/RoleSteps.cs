using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Tenancy;

namespace Staffora.Test.Integration.Infrastructure;

public static class RoleSteps
{
    public static TenantRole Draft(string name, params RolePermission[] permissions) =>
        new() { Name = name, State = TenantRoleState.Create, Permissions = [.. permissions.Select(permission => new TenantRolePermission { Permission = permission })] };

    public static Task<Result<Guid>> CreateAsync(this TenantRoleService service, TenantRole role, CancellationToken cancellationToken) =>
        service.CreateTenantRoleAsync(role, TenantRoleWorkflowAttribute.CreateStep, cancellationToken);

    public static async Task<Result> EditRoleAsync(this TenantRoleService service, Guid roleId, Action<TenantRole> edit, CancellationToken cancellationToken)
    {
        var loaded = await service.LoadTenantRoleUpdaterAsync(roleId, cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return Result.Failure(loaded.Error);
        }

        edit(loaded.Value.Entity);
        return await service.UpdateTenantRoleAndTransitionAsync(loaded.Value, TenantRoleWorkflowAttribute.SaveStep, null, cancellationToken);
    }

    public static async Task<Result> DeleteRoleAsync(this TenantRoleService service, Guid roleId, CancellationToken cancellationToken)
    {
        var loaded = await service.LoadTenantRoleUpdaterAsync(roleId, cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return Result.Failure(loaded.Error);
        }

        return await service.UpdateTenantRoleAndTransitionAsync(loaded.Value, TenantRoleWorkflowAttribute.DeleteStep, null, cancellationToken);
    }

    public static void Grant(this TenantRole role, RolePermission permission) =>
        role.Permissions.Add(new TenantRolePermission { TenantId = role.TenantId, Permission = permission });

    public static void Revoke(this TenantRole role, RolePermission permission)
    {
        foreach (var row in role.Permissions.Where(row => row.Permission == permission).ToList())
        {
            role.Permissions.Remove(row);
        }
    }
}
