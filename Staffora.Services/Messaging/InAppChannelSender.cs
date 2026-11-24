using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Services.Messaging;

public sealed class InAppChannelSender : IChannelSender
{
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly NoticeBus bus;
    private readonly TimeProvider clock;
    private readonly ILogger<InAppChannelSender> log;

    public InAppChannelSender(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, NoticeBus bus, TimeProvider clock, ILogger<InAppChannelSender> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.bus = bus;
        this.clock = clock;
        this.log = log;
    }

    public MessageChannel Channel => MessageChannel.InApp;

    public async Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScopeFor(this.runtime, message.TenantId, "deliver a notice a step wrote into a tenant");

        var (id, tenantId) = (message.Id, message.TenantId);
        var row = await context.OutgoingMessages.FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.TenantId == tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"The outgoing message {id} was not found in its tenant.");
        if (row.Status != OutgoingMessageStatus.Pending)
        {
            this.log.LogInformation("The outgoing message {MessageId} is {Status} already; it is not delivered again.", id, row.Status);
            return;
        }

        var recipients = await ResolveRecipientsAsync(context, row.TenantId, row.RecipientUserIds, row.RecipientPermission, cancellationToken, row.NamedRecipientsHold, row.FallbackPermission);
        var stored = new StoredMessage
        {
            TenantId = row.TenantId,
            OutgoingMessageId = row.Id,
            MessageType = row.MessageType,
            Category = row.Category,
            Subject = row.Subject,
            Text = row.Text,
            Link = row.Link,
            EntityType = row.EntityType,
            EntityId = row.EntityId,
            HistoryEntryId = row.HistoryEntryId,
            Recipients = [.. recipients.Select(userId => new MessageRecipient { TenantId = row.TenantId, UserId = userId })],
        };
        context.StoredMessages.Add(stored);
        row.Status = OutgoingMessageStatus.Delivered;
        row.Delivered = this.clock.GetUtcNow().UtcDateTime;
        row.RecipientCount = recipients.Count;
        StampOnTheTenantsBehalf(context);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var userId in recipients)
        {
            this.bus.Publish(row.TenantId, userId, new NoticeArrived(stored.Id));
        }
    }

    public static async Task<IReadOnlyList<Guid>> ResolveRecipientsAsync(ApplicationDbContext context, Guid tenantId, IReadOnlyCollection<Guid> named, RolePermission? permission, CancellationToken cancellationToken, RolePermission? namedMustHold = null, RolePermission? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(named);
        var recipients = new HashSet<Guid>();
        if (named.Count > 0)
        {
            var ids = named.Distinct().ToList();
            var people = await context.Users.AsNoTracking().ExceptSystemAccounts()
                .Where(user => ids.Contains(user.Id) && user.TenantId == tenantId && user.State == UserState.Active)
                .Select(user => user.Id)
                .ToListAsync(cancellationToken);
            if (namedMustHold is { } held && people.Count > 0)
            {
                var holders = await HoldersOf(context, tenantId, held).Where(userId => people.Contains(userId)).ToListAsync(cancellationToken);
                people = [.. people.Where(holders.Contains)];
            }

            recipients.UnionWith(people);
        }

        if (recipients.Count == 0 && fallback is { } standIn)
        {
            recipients.UnionWith(await HoldersOf(context, tenantId, standIn).ToListAsync(cancellationToken));
        }

        if (permission is { } wanted)
        {
            recipients.UnionWith(await HoldersOf(context, tenantId, wanted).ToListAsync(cancellationToken));
        }

        return [.. recipients];
    }

    private static IQueryable<Guid> HoldersOf(ApplicationDbContext context, Guid tenantId, RolePermission permission) =>
        context.OrganizationUnitUserRoles.AsNoTracking()
            .Where(holding => holding.TenantId == tenantId
                && (holding.OrganizationUnit.State == OrganizationUnitState.Active || holding.OrganizationUnit.UnitType == OrganizationUnitType.Company)
                && holding.TenantRole.State == TenantRoleState.Active
                && holding.User.State == UserState.Active
                && !holding.User.IsSystemAccount
                && holding.User.TenantId == tenantId
                && holding.TenantRole.Permissions.Any(granted => granted.Permission == permission))
            .Select(holding => holding.UserId)
            .Distinct();

    private static void StampOnTheTenantsBehalf(ApplicationDbContext context) => context.StampOnOtherTenantsBehalf();
}
