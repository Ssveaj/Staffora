using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Catalogue;
using Staffora.Services.Integration;
using Staffora.Services.Portal;
using Staffora.Services.Resources;

namespace Staffora.Test.Integration.Infrastructure;

public static class CatalogueScenario
{
    public static ServiceHost HostOf(RealDatabase database, IRuntimeExecutionContext runtime) => ServiceHost.Create(database, runtime, AssignmentScenario.UseClock);

    public static async Task<Guid> QualificationAsync(RealDatabase database, IRuntimeExecutionContext runtime, string name, CancellationToken cancellationToken)
    {
        await using var host = HostOf(database, runtime);
        var created = await host.Get<QualificationService>().CreateQualificationAsync(new Qualification { Name = name }, QualificationWorkflowAttribute.CreateStep, cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    public static async Task<Guid> ServiceAsync(RealDatabase database, IRuntimeExecutionContext runtime, string name, int count, bool offered, CancellationToken cancellationToken, params (Guid Qualification, RequirementLevel Level)[] qualifications)
    {
        await using var host = HostOf(database, runtime);
        var draft = new Service { Name = name, DefaultSlotCount = count, OfferedInPortal = offered };
        foreach (var (qualification, level) in qualifications)
        {
            draft.Qualifications.Add(new ServiceQualification { QualificationId = qualification, Level = level });
        }

        var created = await host.Get<ServiceCatalogueService>().CreateServiceAsync(draft, ServiceWorkflowAttribute.CreateStep, cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    public static async Task<Result> StepServiceAsync(RealDatabase database, IRuntimeExecutionContext runtime, Guid service, string step, CancellationToken cancellationToken, Action<Service>? edit = null)
    {
        await using var host = HostOf(database, runtime);
        var catalogue = host.Get<ServiceCatalogueService>();
        var updater = await catalogue.LoadServiceUpdaterAsync(service, cancellationToken);
        Assert.True(updater.IsSuccess, updater.Error);
        edit?.Invoke(updater.Value!.Entity);
        return await catalogue.UpdateServiceAndTransitionAsync(updater.Value!, step, null, cancellationToken);
    }

    public static async Task DeliverAsync(RealDatabase database, CancellationToken cancellationToken, params Guid[] senders)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            await using (var host = ServiceHost.Create(database, new RuntimeExecutionContext(), services => services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>()))
            {
                await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(cancellationToken);
            }

            await using var system = database.CreateSystemContext();
            if (!await system.IntegrationMessages.AnyAsync(message => senders.Contains(message.TenantId) && message.State == IntegrationMessageState.Pending, cancellationToken))
            {
                return;
            }
        }

        Assert.Fail("The scenario's messages were not all delivered.");
    }

    public static async Task<IReadOnlyList<OfferedServiceChoice>> ChoicesAsync(RealDatabase database, IRuntimeExecutionContext runtime, CancellationToken cancellationToken)
    {
        await using var host = HostOf(database, runtime);
        var options = await host.Get<OrderService>().GetOrderEditorOptionsAsync(null, cancellationToken);
        Assert.True(options.IsSuccess, options.Error);
        return options.Value!.Services;
    }

    public static async Task<Guid> DraftAsync(RealDatabase database, IRuntimeExecutionContext runtime, string name, CancellationToken cancellationToken)
    {
        await using var host = HostOf(database, runtime);
        var orders = host.Get<OrderService>();
        var fresh = await orders.NewOrderAsync(cancellationToken);
        Assert.True(fresh.IsSuccess, fresh.Error);
        var order = fresh.Value!;
        order.Name = name;

        order.Schedule = JobSchedule.Recurring;
        order.PeriodEnd = order.PeriodStart.AddMonths(3);
        var created = await orders.CreateOrderAsync(order, OrderWorkflowAttribute.SaveDraftStep, cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    public static async Task<Result> SaveLinesAsync(RealDatabase database, IRuntimeExecutionContext runtime, Guid order, CancellationToken cancellationToken, params (Guid Copy, int Count)[] lines)
    {
        await using var host = HostOf(database, runtime);
        var orders = host.Get<OrderService>();
        var updater = await orders.LoadOrderUpdaterAsync(order, cancellationToken);
        Assert.True(updater.IsSuccess, updater.Error);
        var entity = updater.Value!.Entity;
        entity.Lines.Clear();
        foreach (var (copy, count) in lines)
        {
            entity.Lines.Add(new OrderLine { TenantId = entity.TenantId, OrderId = entity.Id, OfferedServiceId = copy, Count = count });
        }

        return await orders.UpdateOrderAndTransitionAsync(updater.Value!, OrderWorkflowAttribute.SaveStep, null, cancellationToken);
    }

    public static async Task<Result> StepOrderAsync(RealDatabase database, IRuntimeExecutionContext runtime, Guid order, string step, CancellationToken cancellationToken)
    {
        await using var host = HostOf(database, runtime);
        var orders = host.Get<OrderService>();
        var updater = await orders.LoadOrderUpdaterAsync(order, cancellationToken);
        Assert.True(updater.IsSuccess, updater.Error);
        return await orders.UpdateOrderAndTransitionAsync(updater.Value!, step, null, cancellationToken);
    }

    public static async Task<List<OfferedService>> CopiesAsync(RealDatabase database, Guid portalTenantId, CancellationToken cancellationToken)
    {
        await using var system = database.CreateSystemContext();
        return await system.OfferedServices.AsNoTracking().Where(copy => copy.TenantId == portalTenantId).OrderBy(copy => copy.Name).ThenBy(copy => copy.Id).ToListAsync(cancellationToken);
    }
}
