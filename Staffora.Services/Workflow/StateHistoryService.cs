using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Messaging;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Settings;

namespace Staffora.Services.Workflow;

public sealed class StateHistoryService
{
    public const int MaxPageSize = 50;

    public const string OutsideActor = "Someone outside this organization";

    public const string PlatformOwnerSuffix = " (platform owner)";

    public const string ImpersonatedNote = "Taken by a platform owner signed in as this user.";

    public const string OnBehalfNote = "Recorded on this organization's behalf by its system account.";

    public const string InAppChannel = "in-app";

    public const string SystemAccountName = $"{SystemAccount.FirstName} {SystemAccount.LastName}";

    public const string OwnersOnly = "Only a system owner of this organization or a platform owner can read the history.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly TenantClock tenantClock;
    private readonly ILogger<StateHistoryService> log;

    public StateHistoryService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, TenantClock tenantClock, ILogger<StateHistoryService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.tenantClock = tenantClock;
        this.log = log;
    }

    public async Task<bool> MayReadHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            return await MayReadHistoryOfAsync(context, this.runtime.Current, tenantId: null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading whether the session may read histories failed; it is answered no.");
            return false;
        }
    }

    public async Task<Result<StateHistoryPage>> GetHistoryAsync<TEntity>(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken = default)
        where TEntity : class, IStateEntity
    {
        ArgumentNullException.ThrowIfNull(query);
        if (this.runtime.EffectiveUserId is null)
        {
            return Result<StateHistoryPage>.Failure("Only a signed-in user reads a history.");
        }

        if (query.OlderThan is not null && query.NewerThan is not null)
        {
            return Result<StateHistoryPage>.Failure("A page of the history is older or newer than one step, not both.");
        }

        var take = Math.Clamp(query.Take, 1, MaxPageSize);
        try
        {
            var session = this.runtime.Current;
            await using var context = this.dbFactory.CreateDbContext(this.runtime);

            if (!await MayReadHistoryOfAsync(context, session, tenantId: null, cancellationToken))
            {
                return Result<StateHistoryPage>.Failure(OwnersOnly);
            }

            using var lease = context.BeginPlatformScope(this.runtime, $"the state history of a {typeof(TEntity).Name} in any tenant");

            var entity = await context.Set<TEntity>().AsNoTracking().FirstOrDefaultAsync(row => EF.Property<Guid>(row, nameof(IStateEntity.Id)) == entityId, cancellationToken);
            if (entity is null)
            {
                return Result<StateHistoryPage>.Failure($"The {typeof(TEntity).Name} was not found.");
            }

            var tenantId = TenantOf(entity, session.TenantId);

            if (!await MayReadHistoryOfAsync(context, session, tenantId, cancellationToken))
            {
                return Result<StateHistoryPage>.Failure(OwnersOnly);
            }
            var entityType = context.Model.FindEntityType(typeof(TEntity))?.ClrType.Name ?? typeof(TEntity).Name;
            var history = context.StateHistoryEntries.AsNoTracking()
                .Where(entry => entry.TenantId == tenantId && entry.EntityType == entityType && entry.EntityId == entityId);
            var total = await history.CountAsync(cancellationToken);
            List<Core.Entities.Workflow.StateHistoryEntry> entries;
            if (query.NewerThan is { } newer)
            {
                var (created, id) = (newer.Created, newer.Id);
                entries = await history
                    .Where(entry => entry.Created > created || (entry.Created == created && entry.Id.CompareTo(id) > 0))
                    .OrderBy(entry => entry.Created)
                    .ThenBy(entry => entry.Id)
                    .Take(take)
                    .ToListAsync(cancellationToken);
                entries.Reverse();
            }
            else
            {
                var older = history;
                if (query.OlderThan is { } cursor)
                {
                    var (created, id) = (cursor.Created, cursor.Id);
                    older = older.Where(entry => entry.Created < created || (entry.Created == created && entry.Id.CompareTo(id) < 0));
                }

                entries = await older
                    .OrderByDescending(entry => entry.Created)
                    .ThenByDescending(entry => entry.Id)
                    .Take(take)
                    .ToListAsync(cancellationToken);
            }

            var newest = entries.Count > 0 ? new StateHistoryCursor(entries[0].Created, entries[0].Id) : query.OlderThan;
            var newerCount = 0;
            if (newest is not null)
            {
                var (created, id) = (newest.Created, newest.Id);
                newerCount = await history.CountAsync(entry => entry.Created > created || (entry.Created == created && entry.Id.CompareTo(id) > 0), cancellationToken);
            }

            var ids = entries.Select(entry => entry.ActorId).Distinct().ToList();
            var people = await context.Users.AsNoTracking()
                .Where(user => ids.Contains(user.Id) && user.TenantId == tenantId)
                .Select(user => new { user.Id, user.FirstName, user.LastName, user.IsSystemAccount })
                .ToDictionaryAsync(user => user.Id, user => user.IsSystemAccount ? SystemAccountName : NameOf(user.FirstName, user.LastName), cancellationToken);

            if (lease is not null)
            {
                var outside = ids.Where(id => !people.ContainsKey(id)).ToList();
                if (outside.Count > 0)
                {
                    var adminRoles = context.Roles.Where(role => role.Name == AppRoles.Admin).Select(role => role.Id);
                    var owners = await context.Users.AsNoTracking()
                        .Where(user => outside.Contains(user.Id) && !user.IsSystemAccount
                            && context.Tenants.Any(tenant => tenant.Id == user.TenantId && tenant.IsRootTenant)
                            && context.UserRoles.Any(holding => holding.UserId == user.Id && adminRoles.Contains(holding.RoleId)))
                        .Select(user => new { user.Id, user.FirstName, user.LastName })
                        .ToListAsync(cancellationToken);
                    foreach (var owner in owners)
                    {
                        people[owner.Id] = $"{NameOf(owner.FirstName, owner.LastName)}{PlatformOwnerSuffix}";
                    }
                }
            }

            var zone = await this.tenantClock.GetTimeZoneOfAsync(tenantId, cancellationToken);
            if (!zone.IsSuccess || zone.Value is null)
            {
                return Result<StateHistoryPage>.Failure(zone.Error ?? "The organization's time zone could not be read.");
            }

            var entryIds = entries.Select(entry => entry.Id).ToList();
            var sent = (await context.OutgoingMessages.AsNoTracking()
                    .Where(message => message.TenantId == tenantId && entryIds.Contains(message.HistoryEntryId))
                    .OrderBy(message => message.Created)
                    .ThenBy(message => message.Id)
                    .Select(message => new { message.HistoryEntryId, message.MessageType, message.Subject, message.Status, message.RecipientCount })
                    .ToListAsync(cancellationToken))
                .ToLookup(message => message.HistoryEntryId, message => new StateHistoryNotice(
                    MessageTypes.All.FirstOrDefault(type => type.Key == message.MessageType)?.Name ?? message.Subject,
                    InAppChannel,
                    message.Status,
                    message.RecipientCount));

            var workflow = WorkflowRegistry.Default.Get(typeof(TEntity));
            var steps = entries.Select(entry => StepOf(entry, workflow, zone.Value, people) with { Notices = [.. sent[entry.Id]] }).ToList();
            return Result<StateHistoryPage>.Success(new StateHistoryPage(steps, total, newerCount, zone.Value.Id, InReadersZone: session.TenantId == tenantId, Today: this.tenantClock.TodayIn(zone.Value)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the history of {EntityType} {EntityId} failed.", typeof(TEntity).Name, entityId);
            return Result<StateHistoryPage>.Failure($"The history of the {typeof(TEntity).Name} could not be read. Try again.");
        }
    }

    private static async Task<bool> MayReadHistoryOfAsync(ApplicationDbContext context, RuntimeSnapshot session, Guid? tenantId, CancellationToken cancellationToken)
    {
        if (session.HasPlatformReach())
        {
            return true;
        }

        var tenant = tenantId ?? session.TenantId;
        return tenant is { } owned && session.TenantId == owned && session.EffectiveUserId is { } self
            && await SystemOwners.IsActiveOwnerAsync(context, owned, self, cancellationToken);
    }

    private static StateHistoryStep StepOf(Core.Entities.Workflow.StateHistoryEntry entry, IWorkflow workflow, TimeZoneInfo zone, Dictionary<Guid, string> people)
    {
        var from = (Enum)Enum.ToObject(workflow.StateType, entry.FromState);
        var to = (Enum)Enum.ToObject(workflow.StateType, entry.ToState);
        var transition = workflow.Transitions.FirstOrDefault(candidate => string.Equals(candidate.Key, entry.TransitionKey, StringComparison.Ordinal));
        var notes = new List<string>();
        if (entry.ImpersonatedById is not null)
        {
            notes.Add(ImpersonatedNote);
        }

        if (entry.CreatedById != entry.ActorId && entry.CreatedById == SystemAccount.IdFor(entry.TenantId))
        {
            notes.Add(OnBehalfNote);
        }

        return new StateHistoryStep(
            entry.Id,
            entry.TransitionKey,
            transition?.Label ?? DisplayNameOf(to),
            from,
            to,
            TenantClock.ToLocal(entry.Created, zone),
            people.TryGetValue(entry.ActorId, out var name) ? name : OutsideActor,
            notes,
            new StateHistoryCursor(entry.Created, entry.Id));
    }

    private static Guid TenantOf(IStateEntity entity, Guid? readersTenant) => entity switch
    {
        ITenantEntity row => row.TenantId,
        Tenant tenant => tenant.Id,
        Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition { TenantId: { } fieldTenant } => fieldTenant,
        Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition => readersTenant ?? throw new NotSupportedException("A global custom field's history is read by a session with a tenant."),
        _ => throw new NotSupportedException($"{entity.GetType().Name} is neither a tenant row nor a tenant, so its history has no tenant."),
    };

    private static string NameOf(string firstName, string lastName) =>
        $"{firstName} {lastName}".Trim() is { Length: > 0 } name ? name : "A user of this organization";

    private static string DisplayNameOf(Enum value) =>
        value.GetType().GetField(value.ToString())?.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? value.ToString();
}
