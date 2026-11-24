using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Services.Authorization;

namespace Staffora.Services.Home;

public sealed partial class TenantHomeService
{
    public const string ChecklistNoView = "You do not have permission to view the setup checklist.";

    public const string ChecklistNoTenant = "The setup checklist is read in a tenant of one's own.";

    public const string ChecklistReadFailed = "The setup checklist could not be read. Try again.";

    public const string People = "people";
    public const string StaffMembers = "staff";
    public const string Qualifications = "qualifications";
    public const string WorkPatterns = "work-patterns";
    public const string Customers = "customers";

    public async Task<Result<SetupChecklist>> ReadSetupChecklistAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.TenantView, cancellationToken))
            return Result<SetupChecklist>.Failure(ChecklistNoView);

        var session = this.runtime.Current;
        if (session.HasPlatformReach() || session.TenantId is not { } tenantId)
            return Result<SetupChecklist>.Failure(ChecklistNoTenant);

        var held = await this.permissionService.GetUserPermissionsAsync(cancellationToken);
        bool Holds(params RolePermission[] any) => any.Any(held.Contains);
        var today = await this.tenantClock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<SetupChecklist>.Failure(today.Error ?? ChecklistReadFailed);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var items = new List<SetupChecklistItem>();
            var staffMembers = context.Resources.AsNoTracking().Where(resource => resource.TenantId == tenantId).Where(StaffServiceRules.InService);

            if (Holds(RolePermission.UsersView))
            {
                var people = await context.Users.AsNoTracking().CountAsync(user => user.TenantId == tenantId && !user.IsSystemAccount && user.State == UserState.Active, cancellationToken);
                items.Add(new SetupChecklistItem(
                    People,
                    "Add your people",
                    people == 1 ? "1 active person" : $"{people} active people",
                    people > 1,
                    "/tenant/management/users",
                    "Users"));
            }

            var inService = Holds(RolePermission.ResourcesView, RolePermission.QualificationsManage, RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns)
                ? await staffMembers.CountAsync(cancellationToken)
                : 0;

            if (Holds(RolePermission.ResourcesView))
            {
                items.Add(new SetupChecklistItem(
                    StaffMembers,
                    "Make someone a staff member",
                    inService == 0 ? "Nobody holds the Staff role yet" : inService == 1 ? "1 staff member in service" : $"{inService} staff members in service",
                    inService > 0,
                    "/staffing/staff",
                    "Staff"));
            }

            if (Holds(RolePermission.ResourcesView, RolePermission.QualificationsManage))
            {
                var catalogue = await context.Qualifications.AsNoTracking().CountAsync(qualification => qualification.TenantId == tenantId && qualification.State == QualificationState.Active, cancellationToken);
                var qualified = await staffMembers.CountAsync(resource => context.ResourceQualifications.Any(holding =>
                    holding.TenantId == tenantId && holding.ResourceId == resource.Id && holding.Qualification.State == QualificationState.Active), cancellationToken);
                items.Add(new SetupChecklistItem(
                    Qualifications,
                    "Give staff members their qualifications",
                    $"{Count(catalogue, "qualification", "qualifications")} in the catalogue, and {qualified} of {inService} staff members hold one",
                    catalogue > 0 && qualified > 0,
                    "/staffing/qualifications",
                    "Qualifications"));
            }

            if (Holds(RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns))
            {
                var published = await context.WorkPatterns.AsNoTracking().CountAsync(pattern => pattern.TenantId == tenantId && pattern.State == WorkPatternState.Published, cancellationToken);
                var day = today.Value;
                var patterned = await staffMembers.CountAsync(resource => context.ResourcePatternAssignments.Any(assignment =>
                    assignment.TenantId == tenantId
                    && assignment.ResourceId == resource.Id
                    && assignment.State == ResourcePatternAssignmentState.Active
                    && (assignment.EffectiveTo == null || assignment.EffectiveTo >= day)), cancellationToken);
                items.Add(new SetupChecklistItem(
                    WorkPatterns,
                    "Give staff members a work pattern",
                    $"{Count(published, "published work pattern", "published work patterns")}, and {patterned} of {inService} staff members have one",
                    published > 0 && patterned > 0,
                    "/staffing/work-patterns",
                    "Work patterns"));
            }

            if (Holds(RolePermission.CustomersView, RolePermission.CustomersManage))
            {
                var customers = await context.Customers.AsNoTracking().CountAsync(customer => customer.TenantId == tenantId && customer.State == CustomerState.Active, cancellationToken);
                items.Add(new SetupChecklistItem(
                    Customers,
                    "Add a customer",
                    Count(customers, "active customer", "active customers"),
                    customers > 0,
                    "/staffing/customers",
                    "Customers"));
            }

            return Result<SetupChecklist>.Success(new SetupChecklist(items));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the setup checklist of tenant {TenantId} failed.", tenantId);
            return Result<SetupChecklist>.Failure(ChecklistReadFailed);
        }
    }

    private static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
