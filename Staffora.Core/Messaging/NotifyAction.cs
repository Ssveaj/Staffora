using Staffora.Core.Entities;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Actions;

namespace Staffora.Core.Messaging;

public sealed class NotifyAction<TState, TEntity> : IAction<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    private readonly MessageType type;
    private readonly Func<WorkflowContext<TState, TEntity>, IReadOnlyDictionary<string, string?>> values;
    private readonly Func<TEntity, IEnumerable<Guid>>? recipients;
    private readonly RolePermission? recipientPermission;
    private readonly Func<WorkflowContext<TState, TEntity>, bool>? when;
    private readonly RolePermission? namedRecipientsHold;
    private readonly RolePermission? fallbackPermission;
    private readonly bool withComment;

    public NotifyAction(MessageType type, Func<TEntity, IReadOnlyDictionary<string, string?>> values, Func<TEntity, IEnumerable<Guid>>? recipients = null, RolePermission? recipientPermission = null, Func<WorkflowContext<TState, TEntity>, bool>? when = null, RolePermission? namedRecipientsHold = null, RolePermission? fallbackPermission = null, bool withComment = false)
        : this(type, ValuesOfEntity(values), recipients, recipientPermission, when, namedRecipientsHold, fallbackPermission, withComment)
    {
    }

    private NotifyAction(MessageType type, Func<WorkflowContext<TState, TEntity>, IReadOnlyDictionary<string, string?>> values, Func<TEntity, IEnumerable<Guid>>? recipients, RolePermission? recipientPermission, Func<WorkflowContext<TState, TEntity>, bool>? when, RolePermission? namedRecipientsHold, RolePermission? fallbackPermission, bool withComment)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(values);
        if (recipients is null && recipientPermission is null)
        {
            throw new ArgumentException($"A notice of \"{type.Key}\" names its recipients, a permission whose holders receive it, or both.", nameof(recipients));
        }

        if (fallbackPermission is not null && recipients is null)
        {
            throw new ArgumentException($"A notice of \"{type.Key}\" falls back to a permission only for the people it names.", nameof(fallbackPermission));
        }

        _ = MessageCatalogue.TemplateFor(type);
        this.type = type;
        this.values = values;
        this.recipients = recipients;
        this.recipientPermission = recipientPermission;
        this.when = when;
        this.namedRecipientsHold = namedRecipientsHold;
        this.fallbackPermission = fallbackPermission;
        this.withComment = withComment;
    }

    public static NotifyAction<TState, TEntity> FromContext(MessageType type, Func<WorkflowContext<TState, TEntity>, IReadOnlyDictionary<string, string?>> values, Func<TEntity, IEnumerable<Guid>> recipients, Func<WorkflowContext<TState, TEntity>, bool>? when = null, bool withComment = false) =>
        new(type, values, recipients, recipientPermission: null, when, namedRecipientsHold: null, fallbackPermission: null, withComment);

    public MessageType Type => this.type;

    public Task ExecuteAsync(WorkflowContext<TState, TEntity> context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (this.when is not null && !this.when(context))
        {
            return Task.CompletedTask;
        }

        var entity = context.Entity;
        var rendered = MessageCatalogue.Render(this.type, this.values(context));
        if (this.withComment)
        {
            rendered = MessageCatalogue.WithWords(rendered, context.Comment);
        }

        var named = this.recipients?.Invoke(entity).Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        if (named.Count > OutgoingMessage.MaxNamedRecipients)
        {
            throw new InvalidOperationException($"A notice of \"{this.type.Key}\" names {named.Count} people; address more than {OutgoingMessage.MaxNamedRecipients} through a permission.");
        }

        context.OutgoingMessages.Add(new OutgoingMessage
        {
            TenantId = TenantOf(entity),
            MessageType = this.type.Key,
            Category = this.type.Category,
            EntityType = typeof(TEntity).Name,
            EntityId = entity.Id,
            Subject = rendered.Subject,
            Text = rendered.Text,
            Link = rendered.Link,
            RecipientUserIds = named,
            RecipientPermission = this.recipientPermission,
            NamedRecipientsHold = this.namedRecipientsHold,
            FallbackPermission = this.fallbackPermission,
        });
        return Task.CompletedTask;
    }

    public void AddLockCandidates(WorkflowContext<TState, TEntity> context, LockCandidates candidates)
    {
    }

    private static Func<WorkflowContext<TState, TEntity>, IReadOnlyDictionary<string, string?>> ValuesOfEntity(Func<TEntity, IReadOnlyDictionary<string, string?>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return context => values(context.Entity);
    }

    private static Guid TenantOf(TEntity entity) => entity switch
    {
        ITenantEntity row => row.TenantId,
        Tenant tenant => tenant.Id,
        _ => Guid.Empty,
    };
}
