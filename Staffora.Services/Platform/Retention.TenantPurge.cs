using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;

namespace Staffora.Services.Platform;

public sealed partial class Retention
{
    public const string GraceNotOverReason = "The tenant has not been scheduled for deletion longer than its grace period, so nothing of it is removed.";

    private async Task<List<Guid>> TenantsPastGraceAsync(DateTime scheduledBefore, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("Retention: list the ids of the tenants scheduled for deletion longer than their grace period");
        var pendingDeletion = (int)TenantState.PendingDeletion;
        return await context.Tenants.AsNoTracking()
            .Where(tenant => tenant.State == TenantState.PendingDeletion && !tenant.IsRootTenant)
            .Where(tenant => context.StateHistoryEntries
                .Where(entry => entry.TenantId == tenant.Id && entry.EntityType == nameof(Tenant) && entry.EntityId == tenant.Id && entry.ToState == pendingDeletion)
                .Max(entry => (DateTime?)entry.Created) <= scheduledBefore)
            .OrderBy(tenant => tenant.Id)
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<int> PurgeTenantAsync(Guid tenantId, DateTime scheduledBefore, RetentionOptions settings, CancellationToken cancellationToken)
    {
        var platformTenantId = await PlatformTenantIdAsync(cancellationToken)
            ?? throw new InvalidOperationException("The platform tenant does not exist, so no system account archives a purged tenant.");

        await using var scope = this.scopes.CreateAsyncScope();
        var runtime = SystemAccountSession.Apply(scope.ServiceProvider.GetRequiredService<IRuntimeExecutionContext>(), platformTenantId, "retention pass");
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        using var logScope = this.log.BeginScope("Retention purge of tenant {TenantId}", tenantId);

        await using var context = this.dbFactory.CreateDbContext(runtime);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        using var lease = context.BeginSystemScope("Retention: purge a tenant scheduled for deletion longer than its grace period, then archive it");
        using var purge = await TenantPurge.BeginAsync(context, tenantId, cancellationToken);
        if (await ScheduledForDeletionAsync(context, tenantId, cancellationToken) is not { } scheduled || scheduled > scheduledBefore)
        {
            throw new InvalidOperationException(GraceNotOverReason);
        }

        var tenant = await context.Tenants.AsTracking().SingleAsync(row => row.Id == tenantId, cancellationToken);
        await workflows.RunSystemStepInTransactionAsync<TenantState, Tenant>(context, tenant, TenantWorkflowAttribute.PurgedStep, cancellationToken);

        var (removed, kept) = (0, 0);
        foreach (var step in TenantPurge.PlanFor(context.Model))
        {
            var fromStep = 0;
            int batch;
            do
            {
                batch = await TenantPurge.DeleteBatchAsync(context, step, tenantId, settings.BatchSize, cancellationToken);
                fromStep += batch;
            }
            while (batch > 0);

            var left = await TenantPurge.CountAsync(context, step, tenantId, cancellationToken);
            if (left > 0 && step.OutsideReferences.Count == 0)
            {
                throw new InvalidOperationException($"{left} rows of {step.EntityType.DisplayName()} could not be removed, so the purge is rolled back.");
            }

            if (fromStep + left > 0)
            {
                this.log.LogInformation("Retention purge removed {Count} rows of {EntityType} from tenant {TenantId} and kept {Kept}.", fromStep, step.EntityType.DisplayName(), tenantId, left);
            }

            removed += fromStep;
            kept += left;
        }

        var toTenant = 0;
        int messages;
        do
        {
            messages = await TenantPurge.DeleteMessagesToBatchAsync(context, tenantId, settings.BatchSize, cancellationToken);
            toTenant += messages;
        }
        while (messages > 0);

        await transaction.CommitAsync(CancellationToken.None);
        this.log.LogInformation(
            "Retention purged tenant {TenantId}, scheduled for deletion since {Scheduled:o}: removed {Removed} of its rows and {ToTenant} integration messages sent to it, kept {Kept} rows another record names, and archived it.",
            tenantId, scheduled, removed, toTenant, kept);
        return removed + toTenant;
    }

    private async Task<Guid?> PlatformTenantIdAsync(CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("Retention: find the platform tenant, whose system account archives a purged tenant");
        return await context.Tenants.AsNoTracking().Where(tenant => tenant.IsRootTenant).Select(tenant => (Guid?)tenant.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private static Task<DateTime?> ScheduledForDeletionAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        var pendingDeletion = (int)TenantState.PendingDeletion;
        return context.StateHistoryEntries
            .Where(entry => entry.TenantId == tenantId && entry.EntityType == nameof(Tenant) && entry.EntityId == tenantId && entry.ToState == pendingDeletion)
            .MaxAsync(entry => (DateTime?)entry.Created, cancellationToken);
    }
}
