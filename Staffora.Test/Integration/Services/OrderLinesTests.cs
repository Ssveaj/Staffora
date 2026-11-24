using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Integration;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class OrderLinesTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public OrderLinesTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_order_of_two_lines_is_sent_as_a_job_with_a_slot_per_person_made_from_the_suppliers_own_services()
    {
        var staffing = await AssignmentScenario.CreateAsync(this.database, "Lines Staffing", Ct);
        var keeper = staffing.StaffMembers.Onboarded.RuntimeOf(staffing.Absence.Schedule.Admin);
        var cleaning = await CatalogueScenario.QualificationAsync(this.database, keeper, "Cleaning", Ct);
        var firstAid = await CatalogueScenario.QualificationAsync(this.database, keeper, "First aid", Ct);

        var cleaner = await CatalogueScenario.ServiceAsync(this.database, keeper, "Cleaner", 2, offered: true, Ct, (cleaning, RequirementLevel.Required));
        var root = staffing.StaffMembers.RootId;
        var portal = await PortalScenario.ForCustomerAsync(this.database, staffing.StaffMembers.Onboarded, staffing.Customer, "Brightpath Learning", root, root, Ct);
        var supervisor = await CatalogueScenario.ServiceAsync(this.database, keeper, "Cleaning supervisor", 1, offered: true, Ct, (cleaning, RequirementLevel.Required), (firstAid, RequirementLevel.Preferred));
        await CatalogueScenario.DeliverAsync(this.database, Ct, staffing.TenantId);

        var copies = await CatalogueScenario.CopiesAsync(this.database, portal.PortalTenantId, Ct);
        Assert.Equal([("Cleaner", 2, true), ("Cleaning supervisor", 1, true)], copies.Select(copy => (copy.Name, copy.DefaultSlotCount, copy.Offered)));
        await using (var system = this.database.CreateSystemContext())
        {
            var links = await system.Services.AsNoTracking().Where(service => service.TenantId == staffing.TenantId).ToDictionaryAsync(service => service.Id, service => service.CrossTenantId, Ct);
            Assert.Equal(new[] { links[cleaner], links[supervisor] }.Order(), copies.Select(copy => copy.LinkedCrossTenantId).Order());
            Assert.DoesNotContain(copies, copy => copy.LinkedCrossTenantId == cleaner || copy.LinkedCrossTenantId == supervisor);
        }

        var orderer = await portal.AddOrdererAsync(this.database, "Olive", Ct);
        var ordering = portal.RuntimeOf(orderer);
        Assert.Equal(["Cleaner", "Cleaning supervisor"], (await CatalogueScenario.ChoicesAsync(this.database, ordering, Ct)).Select(choice => choice.Name));
        var order = await CatalogueScenario.DraftAsync(this.database, ordering, "Office Park", Ct);
        var saved = await CatalogueScenario.SaveLinesAsync(this.database, ordering, order, Ct, (copies[0].Id, 2), (copies[1].Id, 1));
        Assert.True(saved.IsSuccess, saved.Error);
        var sent = await CatalogueScenario.StepOrderAsync(this.database, ordering, order, OrderWorkflowAttribute.SendStep, Ct);
        Assert.True(sent.IsSuccess, sent.Error);

        await using var context = this.database.CreateSystemContext();
        var job = await context.Jobs.AsNoTracking().SingleAsync(row => row.TenantId == staffing.TenantId && row.Name == "Office Park", Ct);
        var slots = await context.JobSlots.AsNoTracking().Include(slot => slot.Qualifications).Where(slot => slot.JobId == job.Id).OrderBy(slot => slot.Name).ThenBy(slot => slot.Id).ToListAsync(Ct);
        Assert.Equal(
            [("Cleaner", (Guid?)cleaner), ("Cleaner", cleaner), ("Cleaning supervisor", supervisor)],
            slots.Select(slot => (slot.Name, slot.ServiceId)));
        Assert.All(slots, slot => Assert.Equal((JobSlotState.Active, staffing.TenantId, SystemAccount.IdFor(staffing.TenantId)), (slot.State, slot.TenantId, slot.CreatedById)));
        Assert.All(slots.Take(2), slot => Assert.Equal([(cleaning, RequirementLevel.Required)], slot.Qualifications.Select(row => (row.QualificationId, row.Level))));
        Assert.Equal(
            new[] { (cleaning, RequirementLevel.Required), (firstAid, RequirementLevel.Preferred) }.OrderBy(row => row.Item1),
            slots[2].Qualifications.OrderBy(row => row.QualificationId).Select(row => (row.QualificationId, row.Level)));
        var ids = slots.Select(slot => slot.Id).ToList();
        var history = await context.StateHistoryEntries.AsNoTracking().Where(entry => ids.Contains(entry.EntityId)).ToListAsync(Ct);
        Assert.Equal(3, history.Count);
        Assert.All(history, entry => Assert.Equal((JobSlotWorkflowAttribute.ReceiveStep, staffing.TenantId, SystemAccount.IdFor(staffing.TenantId)), (entry.TransitionKey, entry.TenantId, entry.ActorId)));

        var lines = await context.OrderLines.AsNoTracking().Where(line => line.OrderId == order).OrderBy(line => line.Count).Select(line => line.Count).ToListAsync(Ct);
        Assert.Equal([1, 2], lines);
    }

    [Fact]
    public async Task A_portals_copy_follows_the_service_changed_withdrawn_and_restored_and_nothing_else_crosses()
    {
        var staffing = await AssignmentScenario.CreateAsync(this.database, "Offer Staffing", Ct);
        var keeper = staffing.StaffMembers.Onboarded.RuntimeOf(staffing.Absence.Schedule.Admin);
        var root = staffing.StaffMembers.RootId;
        var portal = await PortalScenario.ForCustomerAsync(this.database, staffing.StaffMembers.Onboarded, staffing.Customer, "Brightpath Learning", root, root, Ct);
        var cleaning = await CatalogueScenario.QualificationAsync(this.database, keeper, "Cleaning", Ct);

        await CatalogueScenario.ServiceAsync(this.database, keeper, "Internal audit", 1, offered: false, Ct);
        var windows = await CatalogueScenario.ServiceAsync(this.database, keeper, "Window cleaning", 2, offered: true, Ct);
        await CatalogueScenario.DeliverAsync(this.database, Ct, staffing.TenantId);
        Assert.Equal([("Window cleaning", 2, true)], (await CatalogueScenario.CopiesAsync(this.database, portal.PortalTenantId, Ct)).Select(copy => (copy.Name, copy.DefaultSlotCount, copy.Offered)));

        var before = await MessagesAsync(staffing.TenantId);
        Assert.True((await CatalogueScenario.StepServiceAsync(this.database, keeper, windows, ServiceWorkflowAttribute.SaveStep, Ct, service => service.Qualifications.Add(service.NewRequirement(cleaning)))).IsSuccess);
        Assert.Equal(before, await MessagesAsync(staffing.TenantId));

        Assert.True((await CatalogueScenario.StepServiceAsync(this.database, keeper, windows, ServiceWorkflowAttribute.SaveStep, Ct, service => (service.Name, service.DefaultSlotCount) = ("Windows and frames", 3))).IsSuccess);
        await CatalogueScenario.DeliverAsync(this.database, Ct, staffing.TenantId);
        Assert.Equal([("Windows and frames", 3, true)], (await CatalogueScenario.CopiesAsync(this.database, portal.PortalTenantId, Ct)).Select(copy => (copy.Name, copy.DefaultSlotCount, copy.Offered)));
        Assert.True((await CatalogueScenario.StepServiceAsync(this.database, keeper, windows, ServiceWorkflowAttribute.ArchiveStep, Ct)).IsSuccess);
        await CatalogueScenario.DeliverAsync(this.database, Ct, staffing.TenantId);
        Assert.Equal([("Windows and frames", false)], (await CatalogueScenario.CopiesAsync(this.database, portal.PortalTenantId, Ct)).Select(copy => (copy.Name, copy.Offered)));
        Assert.Empty(await CatalogueScenario.ChoicesAsync(this.database, portal.AdministratorRuntime(), Ct));
        Assert.True((await CatalogueScenario.StepServiceAsync(this.database, keeper, windows, ServiceWorkflowAttribute.RestoreStep, Ct)).IsSuccess);
        await CatalogueScenario.DeliverAsync(this.database, Ct, staffing.TenantId);
        Assert.Equal([("Windows and frames", true)], (await CatalogueScenario.CopiesAsync(this.database, portal.PortalTenantId, Ct)).Select(copy => (copy.Name, copy.Offered)));

        await using var system = this.database.CreateSystemContext();
        var link = await system.Services.AsNoTracking().Where(service => service.Id == windows).Select(service => service.CrossTenantId).SingleAsync(Ct);
        var messages = await system.IntegrationMessages.AsNoTracking().Where(message => message.TenantId == staffing.TenantId && message.Kind.StartsWith("service-")).OrderBy(message => message.Sequence).ToListAsync(Ct);
        Assert.Equal([ServiceOfferSync.OfferedKind, ServiceOfferSync.ChangedKind, ServiceOfferSync.WithdrawnKind, ServiceOfferSync.OfferedKind], messages.Select(message => message.Kind));
        Assert.All(messages, message => Assert.Equal((portal.PortalTenantId, ServiceOfferSync.CorrelationFor(link, portal.PortalTenantId), IntegrationMessageState.Delivered), (message.TargetTenantId, message.CorrelationId, message.State)));
        var copy = await system.OfferedServices.AsNoTracking().SingleAsync(row => row.TenantId == portal.PortalTenantId, Ct);
        Assert.Equal(SystemAccount.IdFor(portal.PortalTenantId), copy.CreatedById);
    }

    private async Task<int> MessagesAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.IntegrationMessages.AsNoTracking().CountAsync(message => message.TenantId == tenantId, Ct);
    }
}
