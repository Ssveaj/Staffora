using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Portal;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

public sealed partial class OrderSyncTests
{
    [Fact]
    public async Task After_a_full_round_trip_the_read_sweep_and_the_stranger_sweep_hold()
    {
        var s = await this.ScenarioAsync("Round Trip Staffing");
        var tenants = new[] { s.SupplierId, s.PortalId };

        var role = await this.FirstRoleAsync(s.SupplierId);
        var party = await this.SupplierPartyAsync(s.PortalId);
        var partyEditor = await this.EditorOfPartyAsync(party);
        await this.StampAsync("TenantRoles", "CreatedById", role.Id, s.OrdererId);
        await this.StampAsync("Suppliers", "ModifiedById", party, s.Coordinator);
        Assert.Equal(
            ["Supplier.ModifiedById: 1 row(s) written by a stranger, such as " + s.Coordinator.ToString().ToUpperInvariant(), "TenantRole.CreatedById: 1 row(s) written by a stranger, such as " + s.OrdererId.ToString().ToUpperInvariant()],
            (await TenantSweeps.StrangersAsync(this.database, tenants, Ct)).Order(StringComparer.Ordinal));

        var requestId = await this.SendAsync(s, "Round trip geometry");
        var jobId = await this.JobOfAsync(requestId);

        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        await this.DeliverAsync(s);
        var tess = await s.Staffing.AddStaffAsync(this.database, "Tess", Ct);
        await this.AssignAsync(s, slot, tess);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.ConfirmStep);
        await this.DeliverAsync(s);
        var confirmed = await this.PageAsync(s, requestId);
        Assert.Equal(OrderState.Confirmed, confirmed.State);
        Assert.Equal(["Tess Example"], confirmed.StaffMembers);

        await using (var orderer = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId)))
        {
            var posted = await orderer.Get<OrderService>().AddOrderCommentAsync(requestId, CommentAudience.Customer, "Will she bring the compasses?", Ct);
            Assert.True(posted.IsSuccess, posted.Error);
        }

        await this.DeliverAsync(s);
        await using (var coordinator = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var replied = await coordinator.Get<JobService>().AddJobCommentAsync(jobId, CommentAudience.Customer, "She will.", Ct);
            Assert.True(replied.IsSuccess, replied.Error);
        }

        await this.DeliverAsync(s);
        Assert.Equal(2, (await this.ThreadAsync(nameof(Job), jobId)).Count);
        Assert.Equal(2, (await this.ThreadAsync(nameof(Order), requestId)).Count);

        var secondId = await this.SendAsync(s, "Round trip biology");
        var secondJob = await this.JobOfAsync(secondId);
        var canceled = await this.RequestStepAsync(s, secondId, OrderWorkflowAttribute.CancelStepFrom(OrderState.Sent));
        Assert.True(canceled.IsSuccess, canceled.Error);
        await this.DeliverAsync(s);
        Assert.Equal((OrderState.CanceledByUs, JobState.CanceledByCustomer), ((await this.RequestAsync(secondId)).State, (await this.StoredJobAsync(secondJob)).State));

        var crossing = new[] { nameof(Comment), nameof(CommentContainer), nameof(StateHistoryEntry), nameof(IntegrationMessage), nameof(IntegrationReceipt), nameof(OutgoingMessage), nameof(StoredMessage), nameof(MessageRecipient), nameof(ApplicationUser) };
        await using (var system = this.database.CreateSystemContext())
        {
            var stored = await TenantSweeps.TenantsReadAsync(system, Ct);
            Assert.All(crossing, table => Assert.True(stored[table].IsSupersetOf(tenants), $"{table} holds rows of both tenants"));
            Assert.All(new[] { nameof(Job), nameof(CustomerContact), nameof(JobSlot), nameof(SlotAssignment) }, table => Assert.Contains(s.SupplierId, stored[table]));
            Assert.All(new[] { nameof(Order), nameof(OrderStaff), nameof(Supplier) }, table => Assert.Contains(s.PortalId, stored[table]));
        }

        foreach (var (own, runtime) in new[] { (s.SupplierId, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)), (s.PortalId, s.Portal.RuntimeOf(s.OrdererId)) })
        {
            await using var reader = this.database.CreateContext(runtime);
            var read = await TenantSweeps.TenantsReadAsync(reader, Ct);
            var leaked = read.Where(table => table.Value.Any(tenantId => tenantId != own)).Select(table => table.Key).ToList();
            Assert.True(leaked.Count == 0, $"A session of {own} read rows of another tenant in: {string.Join(", ", leaked)}");
            Assert.All(crossing, table => Assert.Contains(own, read[table]));
        }

        Assert.Equal(2, (await TenantSweeps.StrangersAsync(this.database, tenants, Ct)).Count);
        await this.StampAsync("TenantRoles", "CreatedById", role.Id, role.CreatedById);
        await this.StampAsync("Suppliers", "ModifiedById", party, partyEditor);
        var strangers = await TenantSweeps.StrangersAsync(this.database, tenants, Ct);
        Assert.True(strangers.Count == 0, string.Join(Environment.NewLine, strangers));
    }

    private async Task<(Guid Id, Guid CreatedById)> FirstRoleAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        var role = await system.TenantRoles.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.Id).Select(row => new { row.Id, row.CreatedById }).FirstAsync(Ct);
        return (role.Id, role.CreatedById);
    }

    private async Task<Guid?> EditorOfPartyAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Suppliers.AsNoTracking().Where(row => row.Id == id).Select(row => row.ModifiedById).SingleAsync(Ct);
    }

    private async Task StampAsync(string table, string column, Guid id, Guid? value)
    {
        await using var connection = new SqlConnection(this.database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE [{table}] SET [{column}] = @value WHERE [Id] = @id";
        command.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }
}
