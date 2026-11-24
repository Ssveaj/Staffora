using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Home;
using Staffora.Services.Portal;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class OrderTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Start = new(2027, 2, 1);
    private static readonly DateOnly End = new(2027, 5, 31);

    private readonly RealDatabase database;

    public OrderTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_orderer_writes_and_saves_a_request()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Requesting Staffing", Ct);
        var orderer = await scenario.AddOrdererAsync(this.database, "Olive", Ct);
        var runtime = scenario.RuntimeOf(orderer);

        await using (var host = ServiceHost.Create(this.database, runtime))
        {
            var service = host.Get<OrderService>();
            var fresh = await service.NewOrderAsync(Ct);
            Assert.True(fresh.IsSuccess, fresh.Error);
            Assert.Equal((OrderState.Create, orderer, scenario.PortalTenantId), (fresh.Value!.State, fresh.Value.OrdererId, fresh.Value.TenantId));
            Assert.Equal(SupplierState.Active, fresh.Value.Supplier.State);
            var options = await service.GetOrderEditorOptionsAsync(null, Ct);
            Assert.True(options.IsSuccess, options.Error);
            Assert.False(options.Value!.FieldsLocked);
            Assert.Equal(await TenantNameAsync(scenario.Supplier.TenantId), options.Value.Supplier);
        }

        var id = await WriteAsync(runtime, "  Algebra for year 9  ", "Two staff members who know the curriculum.");

        var stored = await StoredAsync(id);
        Assert.Equal((OrderState.Registration, scenario.PortalTenantId, orderer, "Algebra for year 9", "Two staff members who know the curriculum.", Start, End), (stored.State, stored.TenantId, stored.OrdererId, stored.Name, stored.Description, stored.PeriodStart, stored.PeriodEnd));
        Assert.Equal(await SupplierPartyAsync(scenario.PortalTenantId), stored.SupplierId);
        Assert.Equal(((Guid?)null, (Guid?)null), (stored.CrossTenantId, stored.LinkedCrossTenantId));
        Assert.Equal((orderer, (Guid?)null), (stored.CreatedById, stored.ImpersonatedById));
        var history = Assert.Single(await HistoryAsync(id));
        Assert.Equal((OrderWorkflowAttribute.SaveDraftStep, orderer), (history.TransitionKey, history.ActorId));

        var saved = await StepAsync(runtime, id, OrderWorkflowAttribute.SaveStep, request => request.Description = "  Three staff members, one of them a mentor.  ");
        Assert.True(saved.IsSuccess, saved.Error);
        stored = await StoredAsync(id);
        Assert.Equal((OrderState.Registration, "Three staff members, one of them a mentor."), (stored.State, stored.Description));
        Assert.Equal(2, (await HistoryAsync(id)).Count);

        await using var reader = ServiceHost.Create(this.database, runtime);
        var details = await reader.Get<OrderService>().ReadRequestAsync(id, Ct);
        Assert.True(details.IsSuccess, details.Error);
        Assert.Equal(("Algebra for year 9", OrderState.Registration, true, (DateTime?)null), (details.Value!.Name, details.Value.State, details.Value.IsOwn, details.Value.Sent));
        Assert.Equal(await TenantNameAsync(scenario.Supplier.TenantId), details.Value.Supplier);
    }

    [Fact]
    public async Task My_requests_shows_the_readers_own_by_default_and_everyones_when_asked_and_the_home_lists_the_readers_latest()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Listing Staffing", Ct);
        var olive = await scenario.AddOrdererAsync(this.database, "Olive", Ct);
        var pete = await scenario.AddOrdererAsync(this.database, "Pete", Ct);
        var olives = new[] { await WriteAsync(scenario.RuntimeOf(olive), "Biology"), await WriteAsync(scenario.RuntimeOf(olive), "Chemistry") };
        var petes = await WriteAsync(scenario.RuntimeOf(pete), "Drama");

        var mine = await QueryAsync(scenario.RuntimeOf(olive), new ListQuery());
        Assert.Equal(olives.Order(), mine.Rows().Select(row => row.Id).Order());
        Assert.All(mine.Rows(), row => Assert.True(row.IsOwn));
        Assert.Equal(1, mine.HiddenCounts[OrderList.Everyone]);

        var everyone = await QueryAsync(scenario.RuntimeOf(olive), new ListQuery().WithToggle(OrderList.Everyone, true));
        Assert.Equal(olives.Append(petes).Order(), everyone.Rows().Select(row => row.Id).Order());
        Assert.Contains(everyone.Rows(), row => row.Id == petes && !row.IsOwn && row.Orderer.StartsWith("Pete", StringComparison.Ordinal));

        Assert.Equal([petes], (await QueryAsync(scenario.RuntimeOf(pete), new ListQuery())).Rows().Select(row => row.Id));

        Assert.Equal([olives[0]], (await QueryAsync(scenario.RuntimeOf(olive), new ListQuery { Search = "Biol" })).Rows().Select(row => row.Id));
        Assert.Empty((await QueryAsync(scenario.RuntimeOf(olive), new ListQuery().WithFilter(OrderList.Status, "sent"))).Rows());

        await using var host = ServiceHost.Create(this.database, scenario.RuntimeOf(olive));
        var home = await host.Get<TenantHomeService>().ReadPortalAsync(Ct);
        Assert.True(home.IsSuccess, home.Error);
        Assert.Equal(2, home.Value!.OwnRequests);
        Assert.Equal(olives.Order(), home.Value.RecentRequests.Select(row => row.Id).Order());
        Assert.Equal(olives[1], home.Value.RecentRequests[0].Id);
    }

    [Fact]
    public async Task Writing_needs_the_portals_permissions_which_no_staffing_company_and_no_console_holds()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Gated Staffing", Ct);
        var orderer = await scenario.AddOrdererAsync(this.database, "Olive", Ct);
        var id = await WriteAsync(scenario.RuntimeOf(orderer), "Physics");

        var reader = await TwoTenantScenario.AddUserAsync(this.database, scenario.Portal, "Reader", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Portal, reader, Ct, RolePermission.OrdersView);
        await using (var host = ServiceHost.Create(this.database, scenario.RuntimeOf(reader)))
        {
            var service = host.Get<OrderService>();
            Assert.True((await service.ReadRequestAsync(id, Ct)).IsSuccess);
            Assert.Equal(OrderService.NoCreate, (await service.NewOrderAsync(Ct)).Error);
            Assert.Equal(OrderService.NoCreate, (await service.CreateOrderAsync(new Order { Name = "Sneaked", PeriodStart = Start, PeriodEnd = End }, OrderWorkflowAttribute.SaveDraftStep, Ct)).Error);
            var updater = await service.LoadOrderUpdaterAsync(id, Ct);
            Assert.True(updater.IsSuccess, updater.Error);
            Assert.Equal(OrderService.NoStep, (await service.UpdateOrderAndTransitionAsync(updater.Value!, OrderWorkflowAttribute.SaveStep, null, Ct)).Error);
            Assert.True((await service.GetOrderEditorOptionsAsync(id, Ct)).Value!.FieldsLocked);
        }

        await TwoTenantScenario.GrantAsync(this.database, scenario.Supplier.Tenant, scenario.Supplier.OwnerId, Ct, RolePermissions.Portal.ToArray());
        await using (var host = ServiceHost.Create(this.database, scenario.Supplier.OwnerRuntime()))
        {
            var service = host.Get<OrderService>();
            Assert.Equal(OrderService.NoView, (await service.QueryRequestsAsync(new ListQuery(), Ct)).Error);
            Assert.Equal(OrderService.NoView, (await service.ReadRequestAsync(id, Ct)).Error);
            Assert.Equal(OrderService.NoView, (await service.LoadOrderUpdaterAsync(id, Ct)).Error);
            Assert.Equal(OrderService.NoCreate, (await service.NewOrderAsync(Ct)).Error);
            Assert.Equal(OrderService.NoCreate, (await service.CreateOrderAsync(new Order { Name = "Sneaked", PeriodStart = Start, PeriodEnd = End }, OrderWorkflowAttribute.SaveDraftStep, Ct)).Error);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.Supplier.Platform.PlatformOwnerRuntime()))
        {
            var service = host.Get<OrderService>();
            Assert.Equal(OrderService.InsidePortalOnly, (await service.QueryRequestsAsync(new ListQuery(), Ct)).Error);
            Assert.Equal(OrderService.InsidePortalOnly, (await service.ReadRequestAsync(id, Ct)).Error);
            Assert.Equal(OrderService.InsidePortalOnly, (await service.NewOrderAsync(Ct)).Error);
        }

        await using var system = this.database.CreateSystemContext();
        Assert.Equal(1, await system.Orders.CountAsync(request => request.TenantId == scenario.PortalTenantId || request.TenantId == scenario.Supplier.TenantId, Ct));
    }

    [Fact]
    public async Task Another_portals_request_reads_as_not_found_and_neither_portal_lists_the_others()
    {
        var first = await PortalScenario.CreateAsync(this.database, "First Staffing", Ct);
        var second = await PortalScenario.CreateAsync(this.database, "Second Staffing", Ct);
        var theirs = await WriteAsync(first.AdministratorRuntime(), "Geography");
        await WriteAsync(second.AdministratorRuntime(), "History");

        await using var host = ServiceHost.Create(this.database, second.AdministratorRuntime());
        var service = host.Get<OrderService>();
        Assert.Equal(OrderService.NotFound, (await service.ReadRequestAsync(theirs, Ct)).Error);
        Assert.Equal(OrderService.NotFound, (await service.LoadOrderUpdaterAsync(theirs, Ct)).Error);
        Assert.Equal(OrderService.NotFound, (await service.GetOrderEditorOptionsAsync(theirs, Ct)).Error);
        var everyone = await service.QueryRequestsAsync(new ListQuery().WithToggle(OrderList.Everyone, true), Ct);
        Assert.True(everyone.IsSuccess, everyone.Error);
        Assert.Equal(["History"], everyone.Value!.Rows().Select(row => row.Name));
    }

    [Fact]
    public async Task A_request_needs_its_job_a_valid_period_and_texts_that_fit_and_takes_nothing_of_its_supplier_or_orderer_from_the_dialog()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Rules Staffing", Ct);
        var orderer = await scenario.AddOrdererAsync(this.database, "Olive", Ct);
        var runtime = scenario.RuntimeOf(orderer);
        await using (var host = ServiceHost.Create(this.database, runtime))
        {
            var service = host.Get<OrderService>();
            Assert.Equal(OrderService.NameMissing, (await service.CreateOrderAsync(new Order { Name = "  ", PeriodStart = Start, PeriodEnd = End }, OrderWorkflowAttribute.SaveDraftStep, Ct)).Error);
            Assert.Equal(OrderService.TextTooLong, (await service.CreateOrderAsync(new Order { Name = new string('x', Order.NameMaxLength + 1), PeriodStart = Start, PeriodEnd = End }, OrderWorkflowAttribute.SaveDraftStep, Ct)).Error);
            Assert.False((await service.CreateOrderAsync(new Order { Name = "Backwards", PeriodStart = End, PeriodEnd = Start }, OrderWorkflowAttribute.SaveDraftStep, Ct)).IsSuccess);
            Assert.Equal(OrderService.CreateBySave, (await service.CreateOrderAsync(new Order { Name = "Straight off", PeriodStart = Start, PeriodEnd = End }, OrderWorkflowAttribute.SendStep, Ct)).Error);

            var created = await service.CreateOrderAsync(new Order { Name = "Forged", PeriodStart = Start, PeriodEnd = End, SupplierId = Guid.NewGuid(), OrdererId = scenario.AdministratorId, CrossTenantId = Guid.NewGuid() }, OrderWorkflowAttribute.SaveDraftStep, Ct);
            Assert.True(created.IsSuccess, created.Error);
            var stored = await StoredAsync(created.Value);
            Assert.Equal((orderer, await SupplierPartyAsync(scenario.PortalTenantId), (Guid?)null), (stored.OrdererId, stored.SupplierId, stored.CrossTenantId));
        }

        await using var system = this.database.CreateSystemContext();
        Assert.Equal(1, await system.Orders.CountAsync(request => request.TenantId == scenario.PortalTenantId, Ct));
    }

    private async Task<Guid> WriteAsync(IRuntimeExecutionContext runtime, string name, string? description = null)
    {
        await using var host = ServiceHost.Create(this.database, runtime);
        var service = host.Get<OrderService>();
        var fresh = await service.NewOrderAsync(Ct);
        Assert.True(fresh.IsSuccess, fresh.Error);
        var request = fresh.Value!;
        (request.Name, request.Description, request.PeriodStart, request.PeriodEnd) = (name, description, Start, End);
        request.Schedule = JobSchedule.Recurring;
        var created = await service.CreateOrderAsync(request, OrderWorkflowAttribute.SaveDraftStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<Staffora.Core.Models.Result> StepAsync(IRuntimeExecutionContext runtime, Guid id, string step, Action<Order>? edit = null)
    {
        await using var host = ServiceHost.Create(this.database, runtime);
        var service = host.Get<OrderService>();
        var updater = await service.LoadOrderUpdaterAsync(id, Ct);
        Assert.True(updater.IsSuccess, updater.Error);
        edit?.Invoke(updater.Value!.Entity);
        return await service.UpdateOrderAndTransitionAsync(updater.Value!, step, null, Ct);
    }

    private async Task<ListPage<OrderListItem>> QueryAsync(IRuntimeExecutionContext runtime, ListQuery query)
    {
        await using var host = ServiceHost.Create(this.database, runtime);
        var page = await host.Get<OrderService>().QueryRequestsAsync(query, Ct);
        Assert.True(page.IsSuccess, page.Error);
        return page.Value!;
    }

    private async Task<Order> StoredAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Orders.AsNoTracking().SingleAsync(request => request.Id == id, Ct);
    }

    private async Task<List<StateHistoryEntry>> HistoryAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == id).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).ToListAsync(Ct);
    }

    private async Task<Guid> SupplierPartyAsync(Guid portalTenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Suppliers.AsNoTracking().Where(supplier => supplier.TenantId == portalTenantId).Select(supplier => supplier.Id).SingleAsync(Ct);
    }

    private async Task<string> TenantNameAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Tenants.AsNoTracking().Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.Name).SingleAsync(Ct);
    }
}

internal static class ListPageRows
{
    public static IReadOnlyList<TRow> Rows<TRow>(this ListPage<TRow> page) => [.. page.Sections.SelectMany(section => section.Rows)];
}
