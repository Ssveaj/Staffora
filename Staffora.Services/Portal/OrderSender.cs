using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Messaging;
using Staffora.Services.Workflow;

namespace Staffora.Services.Portal;

public sealed class OrderSender : IOrderSender
{
    private const string Actor = "order send";

    private readonly Dictionary<Guid, SentJob> sent = [];
    private readonly IServiceScopeFactory scopes;
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly ILogger<OrderSender> log;

    public OrderSender(IServiceScopeFactory scopes, IApplicationDbContextFactory<ApplicationDbContext> dbFactory, ILogger<OrderSender> log)
    {
        this.scopes = scopes;
        this.dbFactory = dbFactory;
        this.log = log;
    }

    public void Link(Order request)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.CrossTenantId ??= Guid.NewGuid();
        request.LinkedCrossTenantId ??= Guid.NewGuid();
    }

    public async Task QueueCancelAsync(DbContext store, Order request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = store as ApplicationDbContext ?? throw new InvalidOperationException("An order's cancellation is queued on the application's data context.");

        if (request.CrossTenantId is not { } correlation)
        {
            return;
        }

        var supplierId = request.SupplierId;
        var supplierTenant = await context.Suppliers.AsNoTracking()
            .Where(supplier => supplier.Id == supplierId)
            .Select(supplier => (Guid?)supplier.LinkedTenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("A sent order names a supplier party its portal holds.");
        IntegrationOutbox.Enqueue(context, OrderSync.CancelKind, supplierTenant, correlation, "{}");
    }

    internal void Sent(Guid requestId, SentJob job) => this.sent[requestId] = job;

    internal void Forget(Guid requestId) => this.sent.Remove(requestId);

    public async Task DeliverAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (!this.sent.Remove(requestId, out var sent))
        {
            return;
        }

        try
        {
            await using var scope = this.scopes.CreateAsyncScope();
            var supplier = SystemAccountSession.Apply(scope.ServiceProvider.GetRequiredService<IRuntimeExecutionContext>(), sent.TenantId, Actor);
            OutgoingMessage? notice;
            await using (var context = this.dbFactory.CreateDbContext(supplier))
            {
                var (noticeId, tenantId) = (sent.NoticeId, sent.TenantId);
                notice = await context.OutgoingMessages.AsNoTracking()
                    .FirstOrDefaultAsync(row => row.Id == noticeId && row.TenantId == tenantId && row.Status == OutgoingMessageStatus.Pending, cancellationToken);
            }

            if (notice is not null)
            {
                await scope.ServiceProvider.GetRequiredService<IOutgoingMessageDispatcher>().DispatchAsync([notice], notice.HistoryEntryId, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            this.log.LogWarning(exception, "Delivering the notice {NoticeId} of the job {JobId} an order sent into tenant {TenantId} failed after the commit; it stays pending and the integration outbox delivers it.", sent.NoticeId, sent.JobId, sent.TenantId);
        }
    }
}

internal sealed record SentJob(Guid TenantId, Guid JobId, Guid NoticeId);

public sealed class OrderSendFollowUp : IStepFollowUp
{
    public const string SupplierUnavailable = "The staffing company cannot take your order right now, so nothing was sent. Try again later.";

    public const string ServiceWithdrawn = "A service in this order is no longer offered, so nothing was sent. Remove it and send again.";

    public const string Reason = "send an order: judge the staffing company's side and write its job, the slots its lines ask for from its own offered services, the orderer's contact and the coordinators' notice into its tenant on its behalf";

    internal const string TenantsNotInUse = "the staffing company's tenant or the link to it is not in use";

    internal const string CustomerNotInUse = "the staffing company's customer is not in use";

    private readonly OrderSender sender;
    private readonly ILogger<OrderSendFollowUp> log;

    public OrderSendFollowUp(OrderSender sender, ILogger<OrderSendFollowUp> log)
    {
        this.sender = sender;
        this.log = log;
    }

    public Type EntityType => typeof(Order);

    public async Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(taken);
        if (entity is not Order request || !taken.Outcome.Steps.Any(step => ReferenceEquals(step.Entity, request) && step.TransitionKey == OrderWorkflowAttribute.SendStep))
        {
            return;
        }

        if (request.CrossTenantId is not { } requestLink || request.LinkedCrossTenantId is not { } jobLink)
        {
            throw new InvalidOperationException("An order is sent only once its step has linked it.");
        }

        var portalTenantId = request.TenantId;
        var orderer = await context.Users.AsNoTracking()
            .Where(user => user.Id == request.OrdererId && user.TenantId == portalTenantId)
            .Select(user => new { user.Id, user.FirstName, user.LastName, user.Email })
            .SingleAsync(cancellationToken);
        var party = await context.Suppliers.AsNoTracking()
            .Where(supplier => supplier.Id == request.SupplierId && supplier.TenantId == portalTenantId)
            .Select(supplier => new { supplier.LinkedTenantId, supplier.State })
            .SingleAsync(cancellationToken);
        var supplierTenantId = party.LinkedTenantId;

        var requestId = request.Id;
        var lines = await context.OrderLines.AsNoTracking()
            .Where(line => line.OrderId == requestId && line.TenantId == portalTenantId)
            .OrderBy(line => line.OfferedService.Name)
            .ThenBy(line => line.OfferedServiceId)
            .Select(line => new OrderedLine(line.OfferedService.LinkedCrossTenantId, line.OfferedService.Name, line.Count))
            .ToListAsync(cancellationToken);

        var portalState = await context.HoldTenantAsync(portalTenantId, cancellationToken);

        using (context.BeginSystemScope(Reason))
        {
            var supplierState = await context.HoldTenantAsync(supplierTenantId, cancellationToken);
            var portalParent = await context.Tenants.AsNoTracking().Where(tenant => tenant.Id == portalTenantId).Select(tenant => tenant.ParentTenantId).SingleAsync(cancellationToken);
            var supplierParent = await context.Tenants.AsNoTracking().Where(tenant => tenant.Id == supplierTenantId).Select(tenant => tenant.ParentTenantId).FirstOrDefaultAsync(cancellationToken);
            if (party.State != SupplierState.Active
                || !TenantStates.ExchangesMessages(portalState)
                || !TenantStates.ExchangesMessages(supplierState)
                || !TenantStates.AreLinked(portalTenantId, portalParent, supplierTenantId, supplierParent)
                || portalParent != supplierTenantId)
            {
                throw Refused(request, TenantsNotInUse);
            }

            try
            {
                await AppLock.TakeInOrderAsync(context, supplierTenantId, [AppLock.Structure, AppLock.Customers, AppLock.Jobs], cancellationToken);
            }
            catch (Exception busy) when (busy is OrganizationStructureException or AppLockBusyException)
            {
                throw new StepFollowUpRefusedException(SupplierUnavailable, busy);
            }

            var customer = await context.Customers.AsNoTracking()
                .Where(row => row.TenantId == supplierTenantId && row.LinkedTenantId == portalTenantId)
                .Select(row => new { row.Id, row.Name, row.State, row.DefaultUnitId })
                .SingleOrDefaultAsync(cancellationToken);
            if (customer is not { State: CustomerState.Active, DefaultUnitId: { } unitId })
            {
                throw Refused(request, CustomerNotInUse);
            }

            var existing = await context.Jobs.AsNoTracking()
                .Where(job => job.TenantId == supplierTenantId && job.LinkedCrossTenantId == requestLink)
                .Select(job => new { job.Id, job.CrossTenantId })
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                if (existing.CrossTenantId != jobLink)
                {
                    throw new InvalidOperationException("The job an order was sent as does not carry the order's link.");
                }

                this.log.LogInformation("The order {RequestId} was sent again; its job {JobId} is there already, so nothing more is written.", request.Id, existing.Id);
                return;
            }

            var services = await OfferedServicesAsync(context, supplierTenantId, lines, cancellationToken);
            if (lines.FirstOrDefault(line => !services.ContainsKey(line.ServiceLink)) is { } withdrawn)
            {
                this.log.LogInformation("The order {RequestId} of tenant {TenantId} was not sent: its line {Service} names a service no longer offered.", request.Id, request.TenantId, withdrawn.Name);
                throw new StepFollowUpRefusedException(ServiceWithdrawn);
            }

            var account = SystemAccount.IdFor(supplierTenantId);

            var paired = await CustomerContactPairing.FindOrAddAsync(context, supplierTenantId, customer.Id, orderer.Id, orderer.FirstName, orderer.LastName, orderer.Email, cancellationToken);
            if (paired.Added)
            {
                context.StampOnOtherTenantsBehalf();
                await context.SaveChangesAsync(cancellationToken);
            }

            var contactId = paired.Id;

            var job = new Job
            {
                TenantId = supplierTenantId,
                Name = request.Name,
                Description = request.Description,
                CustomerId = customer.Id,
                ContactId = contactId,
                OrganizationUnitId = unitId,
                Schedule = request.Schedule,
                PeriodStart = request.PeriodStart,
                PeriodEnd = request.PeriodEnd,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                PlaceName = request.PlaceName,
                StreetAddress = request.StreetAddress,
                PostalCode = request.PostalCode,
                City = request.City,
                CrossTenantId = jobLink,
                LinkedCrossTenantId = requestLink,
                State = JobState.AwaitingCoordination,
                CreatedById = account,
            };
            context.Jobs.Add(job);
            var received = StateHistoryWriter.AddOnBehalfOf(context, supplierTenantId, [new TransitionStep(job, JobState.Create, JobState.AwaitingCoordination, JobWorkflowAttribute.ReceiveStep)])[0];
            context.StampOnOtherTenantsBehalf();
            await context.SaveChangesAsync(cancellationToken);

            var slots = new List<TransitionStep>();
            foreach (var line in lines)
            {
                var service = services[line.ServiceLink];
                for (var person = 0; person < line.Count; person++)
                {
                    var slot = ServiceSlotRules.Build(supplierTenantId, job.Id, service.Template, service.Active);
                    slot.CreatedById = account;
                    context.JobSlots.Add(slot);
                    slots.Add(new TransitionStep(slot, JobSlotState.Create, JobSlotState.Active, JobSlotWorkflowAttribute.ReceiveStep));
                }
            }

            if (slots.Count > 0)
            {
                StateHistoryWriter.AddOnBehalfOf(context, supplierTenantId, slots);
                context.StampOnOtherTenantsBehalf();
                await context.SaveChangesAsync(cancellationToken);
            }

            var rendered = MessageCatalogue.Render(MessageTypes.JobRequested, new Dictionary<string, string?>
            {
                ["Customer"] = customer.Name,
                ["Job"] = job.Name,
                ["Period"] = JobRules.WhenText(JobTimes.Of(job)),
                ["JobId"] = job.Id.ToString(),
            });
            var notice = OutgoingMessageWriter.AddOnBehalfOf(context, supplierTenantId, new OutgoingMessage
            {
                TenantId = supplierTenantId,
                MessageType = MessageTypes.JobRequested.Key,
                Category = MessageTypes.JobRequested.Category,
                EntityType = nameof(Job),
                EntityId = job.Id,
                Subject = rendered.Subject,
                Text = rendered.Text,
                Link = rendered.Link,
                RecipientPermission = RolePermission.PerformCoordination,
            }, received.Id);
            context.StampOnOtherTenantsBehalf();
            await context.SaveChangesAsync(cancellationToken);

            this.sender.Sent(request.Id, new SentJob(supplierTenantId, job.Id, notice.Id));
            this.log.LogInformation("The order {RequestId} of tenant {PortalTenantId} was written as the job {JobId} into tenant {SupplierTenantId}.", request.Id, portalTenantId, job.Id, supplierTenantId);
        }
    }

    private static async Task<Dictionary<Guid, LineService>> OfferedServicesAsync(ApplicationDbContext context, Guid supplierTenantId, IReadOnlyList<OrderedLine> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        var links = lines.Select(line => line.ServiceLink).Distinct().ToList();
        var services = await context.Services.AsNoTracking()
            .Where(service => service.TenantId == supplierTenantId && links.Contains(service.CrossTenantId) && service.State == ServiceState.Active && service.OfferedInPortal)
            .OrderBy(service => service.Id)
            .Select(service => new
            {
                service.Id,
                service.CrossTenantId,
                service.Name,
                service.Description,
                Qualifications = service.Qualifications.Select(named => new { named.QualificationId, named.Level, Active = named.Qualification.State == QualificationState.Active }).ToList(),
            })
            .ToListAsync(cancellationToken);
        return services.ToDictionary(
            service => service.CrossTenantId,
            service => new LineService(
                new ServiceTemplate(service.Id, service.Name, service.Description, [.. service.Qualifications.Select(named => new SlotRequirement(named.QualificationId, named.Level))]),
                service.Qualifications.Where(named => named.Active).Select(named => named.QualificationId).ToHashSet()));
    }

    private sealed record OrderedLine(Guid ServiceLink, string Name, int Count);

    private sealed record LineService(ServiceTemplate Template, IReadOnlySet<Guid> Active);

    private StepFollowUpRefusedException Refused(Order request, string why)
    {
        this.log.LogInformation("The order {RequestId} of tenant {TenantId} was not sent: {Why}.", request.Id, request.TenantId, why);
        return new StepFollowUpRefusedException(SupplierUnavailable);
    }
}
