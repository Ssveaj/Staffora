using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Portal;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class OfferedServiceIsolationTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public OfferedServiceIsolationTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_portal_reads_only_its_own_copies_and_a_line_naming_another_portals_copy_is_refused()
    {
        var (x, y, z) = await ArrangeAsync("Isolated");

        Assert.Equal(["Office cleaning"], (await CatalogueScenario.ChoicesAsync(this.database, x.AdministratorRuntime(), Ct)).Select(choice => choice.Name));
        Assert.Equal(["Office cleaning"], (await CatalogueScenario.ChoicesAsync(this.database, y.AdministratorRuntime(), Ct)).Select(choice => choice.Name));
        Assert.Equal(["Garden care"], (await CatalogueScenario.ChoicesAsync(this.database, z.AdministratorRuntime(), Ct)).Select(choice => choice.Name));

        var xCopy = (await CatalogueScenario.CopiesAsync(this.database, x.PortalTenantId, Ct)).Single();
        var yCopy = (await CatalogueScenario.CopiesAsync(this.database, y.PortalTenantId, Ct)).Single();
        Assert.NotEqual(xCopy.Id, yCopy.Id);
        Assert.Equal(xCopy.LinkedCrossTenantId, yCopy.LinkedCrossTenantId);
        await using (var context = this.database.CreateContext(x.AdministratorRuntime()))
        {
            Assert.Equal([xCopy.Id], await context.OfferedServices.AsNoTracking().Select(copy => copy.Id).ToListAsync(Ct));
            Assert.Empty(await context.Services.AsNoTracking().ToListAsync(Ct));
            Assert.Empty(await context.ServiceQualifications.AsNoTracking().ToListAsync(Ct));
            Assert.Empty(await context.JobSlots.AsNoTracking().ToListAsync(Ct));
        }

        var orderer = await x.AddOrdererAsync(this.database, "Xia", Ct);
        var order = await CatalogueScenario.DraftAsync(this.database, x.RuntimeOf(orderer), "Head office", Ct);
        Assert.Equal(OrderService.LinesInvalid, (await CatalogueScenario.SaveLinesAsync(this.database, x.RuntimeOf(orderer), order, Ct, (yCopy.Id, 1))).Error);
        Assert.True((await CatalogueScenario.SaveLinesAsync(this.database, x.RuntimeOf(orderer), order, Ct, (xCopy.Id, 2))).IsSuccess);
    }

    [Fact]
    public async Task A_withdrawn_service_is_refused_on_send_whether_the_portal_has_heard_or_not()
    {
        var (x, y, _) = await ArrangeAsync("Withdrawn");
        var supplier = x.Supplier;
        var service = await ServiceOfAsync(supplier.TenantId, "Office cleaning");

        var orderer = await x.AddOrdererAsync(this.database, "Wes", Ct);
        var ordering = x.RuntimeOf(orderer);
        var copy = (await CatalogueScenario.CopiesAsync(this.database, x.PortalTenantId, Ct)).Single();
        var heard = await CatalogueScenario.DraftAsync(this.database, ordering, "Heard", Ct);
        var unheard = await CatalogueScenario.DraftAsync(this.database, ordering, "Unheard", Ct);
        Assert.True((await CatalogueScenario.SaveLinesAsync(this.database, ordering, heard, Ct, (copy.Id, 1))).IsSuccess);
        Assert.True((await CatalogueScenario.SaveLinesAsync(this.database, ordering, unheard, Ct, (copy.Id, 1))).IsSuccess);

        Assert.True((await CatalogueScenario.StepServiceAsync(this.database, supplier.OwnerRuntime(), service, ServiceWorkflowAttribute.ArchiveStep, Ct)).IsSuccess);
        Assert.True((await CatalogueScenario.CopiesAsync(this.database, x.PortalTenantId, Ct)).Single().Offered);
        Assert.Equal(OrderSendFollowUp.ServiceWithdrawn, (await CatalogueScenario.StepOrderAsync(this.database, ordering, unheard, OrderWorkflowAttribute.SendStep, Ct)).Error);
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.False(await system.Jobs.AsNoTracking().AnyAsync(job => job.TenantId == supplier.TenantId && job.Name == "Unheard", Ct));
            Assert.Equal(OrderState.Registration, await system.Orders.AsNoTracking().Where(row => row.Id == unheard).Select(row => row.State).SingleAsync(Ct));
        }

        await CatalogueScenario.DeliverAsync(this.database, Ct, supplier.TenantId);
        Assert.False((await CatalogueScenario.CopiesAsync(this.database, x.PortalTenantId, Ct)).Single().Offered);
        Assert.False((await CatalogueScenario.CopiesAsync(this.database, y.PortalTenantId, Ct)).Single().Offered);
        var refused = await CatalogueScenario.StepOrderAsync(this.database, ordering, heard, OrderWorkflowAttribute.SendStep, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Office cleaning is no longer offered", refused.Error, StringComparison.Ordinal);

        Assert.True((await CatalogueScenario.SaveLinesAsync(this.database, ordering, heard, Ct)).IsSuccess);
        Assert.True((await CatalogueScenario.StepOrderAsync(this.database, ordering, heard, OrderWorkflowAttribute.SendStep, Ct)).IsSuccess);
    }

    [Fact]
    public async Task The_limits_of_an_orders_lines_hold_through_the_service_and_on_send_whatever_wrote_them()
    {
        var (x, _, _) = await ArrangeAsync("Limits");
        var supplier = x.Supplier;
        for (var number = 1; number <= OrderLine.MaxLines; number++)
        {
            await CatalogueScenario.ServiceAsync(this.database, supplier.OwnerRuntime(), $"Service {number:00}", 1, offered: true, Ct);
        }

        await CatalogueScenario.DeliverAsync(this.database, Ct, supplier.TenantId);
        var copies = await CatalogueScenario.CopiesAsync(this.database, x.PortalTenantId, Ct);
        Assert.Equal(OrderLine.MaxLines + 1, copies.Count);

        var orderer = await x.AddOrdererAsync(this.database, "Lim", Ct);
        var ordering = x.RuntimeOf(orderer);
        var order = await CatalogueScenario.DraftAsync(this.database, ordering, "Limits", Ct);
        Assert.Equal(OrderService.LinesInvalid, (await CatalogueScenario.SaveLinesAsync(this.database, ordering, order, Ct, (copies[0].Id, OrderLine.MinCount - 1))).Error);
        Assert.Equal(OrderService.LinesInvalid, (await CatalogueScenario.SaveLinesAsync(this.database, ordering, order, Ct, (copies[0].Id, OrderLine.MaxCount + 1))).Error);
        Assert.Equal(OrderService.LinesInvalid, (await CatalogueScenario.SaveLinesAsync(this.database, ordering, order, Ct, [.. copies.Select(copy => (copy.Id, 1))])).Error);
        var ten = await CatalogueScenario.SaveLinesAsync(this.database, ordering, order, Ct, [.. copies.Take(OrderLine.MaxLines).Select(copy => (copy.Id, OrderLine.MaxCount))]);
        Assert.True(ten.IsSuccess, ten.Error);

        await using (var context = this.database.CreateContext(ordering))
        {
            context.OrderLines.Add(new OrderLine { TenantId = x.PortalTenantId, OrderId = order, OfferedServiceId = copies[^1].Id, Count = 1 });
            await context.SaveChangesAsync(Ct);
        }

        var refused = await CatalogueScenario.StepOrderAsync(this.database, ordering, order, OrderWorkflowAttribute.SendStep, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains(OrderWorkflowAttribute.LinesValidReason, refused.Error, StringComparison.Ordinal);
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.Jobs.AsNoTracking().AnyAsync(job => job.TenantId == supplier.TenantId && job.Name == "Limits", Ct));
        Assert.Equal(OrderState.Registration, await system.Orders.AsNoTracking().Where(row => row.Id == order).Select(row => row.State).SingleAsync(Ct));
    }

    private async Task<(PortalScenario X, PortalScenario Y, PortalScenario Z)> ArrangeAsync(string name)
    {
        var x = await PortalScenario.CreateAsync(this.database, $"{name} Company A", Ct, customer: "Portal X");
        var a = x.Supplier;
        var secondCustomer = await PortalScenario.AddActiveCustomerAsync(this.database, a.OwnerRuntime(), TwoTenantScenario.UniqueName("Portal Y"), Ct);
        var y = await PortalScenario.ForCustomerAsync(this.database, a, secondCustomer, "Portal Y", x.RootId, x.RootId, Ct);
        var z = await PortalScenario.CreateAsync(this.database, $"{name} Company B", Ct, customer: "Portal Z");
        await CatalogueScenario.ServiceAsync(this.database, a.OwnerRuntime(), "Office cleaning", 2, offered: true, Ct);
        await CatalogueScenario.ServiceAsync(this.database, z.Supplier.OwnerRuntime(), "Garden care", 1, offered: true, Ct);
        await CatalogueScenario.DeliverAsync(this.database, Ct, a.TenantId, z.Supplier.TenantId);
        return (x, y, z);
    }

    private async Task<Guid> ServiceOfAsync(Guid tenantId, string name)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Services.AsNoTracking().Where(service => service.TenantId == tenantId && service.Name == name).Select(service => service.Id).SingleAsync(Ct);
    }
}
