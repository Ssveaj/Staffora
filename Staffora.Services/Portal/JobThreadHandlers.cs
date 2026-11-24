using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Data;
using Staffora.Services.Integration;
using Staffora.Services.Messaging;
using Staffora.Services.Workflow;

namespace Staffora.Services.Portal;

public sealed class OrderCommentHandler : IIntegrationHandler
{
    private readonly ILogger<OrderCommentHandler> log;

    public OrderCommentHandler(ILogger<OrderCommentHandler> log)
    {
        this.log = log;
    }

    public async Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(context);
        if (JobThreadSync.ReadToSupplier(delivery.Payload) is not { } payload)
        {
            this.log.LogWarning("The thread message {MessageId} of tenant {SourceTenantId} carries no comment; it copies nothing.", delivery.MessageId, delivery.SourceTenantId);
            return;
        }

        foreach (var stepLock in delivery.Services.GetServices<IStepLock>().Where(stepLock => stepLock.EntityType.IsAssignableFrom(typeof(Job))))
        {
            await stepLock.TakeAsync(context, null, cancellationToken);
        }

        var correlation = delivery.CorrelationId;
        var job = await context.Jobs.AsNoTracking().Include(row => row.Customer).FirstOrDefaultAsync(row => row.LinkedCrossTenantId == correlation, cancellationToken);
        if (job is null || job.Customer.LinkedTenantId != delivery.SourceTenantId)
        {
            this.log.LogWarning("The thread message {MessageId} of tenant {SourceTenantId} names no job of tenant {TenantId} its portal sent; it copies nothing.", delivery.MessageId, delivery.SourceTenantId, delivery.TargetTenantId);
            return;
        }

        var source = payload.Comment;
        if (await context.Comments.AsNoTracking().AnyAsync(comment => comment.LinkedCrossTenantId == source, cancellationToken))
        {
            this.log.LogInformation("The portal's comment of message {MessageId} is in the job {JobId}'s customer thread already; nothing more is written.", delivery.MessageId, job.Id);
            return;
        }

        var tenantId = delivery.TargetTenantId;
        var writer = payload.Writer;
        var contact = await CustomerContactPairing.FindOrAddAsync(context, tenantId, job.CustomerId, writer.UserId, writer.FirstName, writer.LastName, writer.Email, cancellationToken);
        var copy = await CommentThreadWriter.AddAsync(
            context,
            job,
            CommentAudience.Customer,
            CommentKind.Comment,
            CommentAuthorType.Contact,
            Held(payload.Text),
            transitionKey: null,
            cancellationToken,
            new CommentCrossing(CrossTenantId: null, LinkedCrossTenantId: source, AuthorContactId: contact.Id));

        var type = MessageTypes.JobCustomerWrote;
        var rendered = MessageCatalogue.Render(type, new Dictionary<string, string?>
        {
            ["Customer"] = job.Customer.Name,
            ["Author"] = string.IsNullOrWhiteSpace(contact.Name) ? "A contact" : contact.Name,
            ["Job"] = job.Name,
            ["JobId"] = job.Id.ToString(),
        });
        var notice = new OutgoingMessage
        {
            TenantId = tenantId,
            MessageType = type.Key,
            Category = type.Category,
            EntityType = nameof(Job),
            EntityId = job.Id,
            Subject = rendered.Subject,
            Text = rendered.Text,
            Link = rendered.Link,
        };
        notice.RecipientUserIds = job.CoordinatorUserId is { } coordinator ? [coordinator] : [];
        notice.NamedRecipientsHold = RolePermission.ViewCoordination;
        notice.FallbackPermission = RolePermission.PerformCoordination;

        var notices = OutgoingMessageWriter.AddForComment(context, [notice], copy.Id);
        var dispatcher = delivery.Services.GetRequiredService<IOutgoingMessageDispatcher>();
        var copyId = copy.Id;
        delivery.AfterCommit.Add(token => dispatcher.DispatchAsync(notices, copyId, token));
    }

    internal static string Held(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= Comment.MaxTextLength ? trimmed : trimmed[..Comment.MaxTextLength];
    }
}

public sealed class JobCommentHandler : IIntegrationHandler
{
    private readonly ILogger<JobCommentHandler> log;

    public JobCommentHandler(ILogger<JobCommentHandler> log)
    {
        this.log = log;
    }

    public async Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(context);
        if (JobThreadSync.ReadToPortal(delivery.Payload) is not { } payload)
        {
            this.log.LogWarning("The thread message {MessageId} of tenant {SourceTenantId} carries no comment; it copies nothing.", delivery.MessageId, delivery.SourceTenantId);
            return;
        }

        var correlation = delivery.CorrelationId;
        var request = await context.Orders.AsNoTracking().Include(row => row.Supplier).FirstOrDefaultAsync(row => row.LinkedCrossTenantId == correlation, cancellationToken);
        if (request is null || request.Supplier.LinkedTenantId != delivery.SourceTenantId)
        {
            this.log.LogWarning("The thread message {MessageId} of tenant {SourceTenantId} names no request of tenant {TenantId} sent to it; it copies nothing.", delivery.MessageId, delivery.SourceTenantId, delivery.TargetTenantId);
            return;
        }

        var source = payload.Comment;
        if (await context.Comments.AsNoTracking().AnyAsync(comment => comment.LinkedCrossTenantId == source, cancellationToken))
        {
            this.log.LogInformation("The staffing company's comment of message {MessageId} is in the request {RequestId}'s messages already; nothing more is written.", delivery.MessageId, request.Id);
            return;
        }

        var copy = await CommentThreadWriter.AddAsync(
            context,
            request,
            CommentAudience.Customer,
            CommentKind.Comment,
            CommentAuthorType.Supplier,
            OrderCommentHandler.Held(payload.Text),
            transitionKey: null,
            cancellationToken,
            new CommentCrossing(CrossTenantId: null, LinkedCrossTenantId: source, AuthorSupplierId: request.SupplierId));

        var type = MessageTypes.OrderSupplierWrote;
        var rendered = MessageCatalogue.Render(type, new Dictionary<string, string?>
        {
            ["Supplier"] = request.Supplier.Name,
            ["Job"] = request.Name,
            ["OrderId"] = request.Id.ToString(),
        });
        var notice = new OutgoingMessage
        {
            TenantId = delivery.TargetTenantId,
            MessageType = type.Key,
            Category = type.Category,
            EntityType = nameof(Order),
            EntityId = request.Id,
            Subject = rendered.Subject,
            Text = rendered.Text,
            Link = rendered.Link,
        };

        notice.RecipientUserIds = [request.OrdererId];
        notice.NamedRecipientsHold = RolePermission.OrdersView;
        notice.FallbackPermission = RolePermission.CustomerUsersManage;

        var notices = OutgoingMessageWriter.AddForComment(context, [notice], copy.Id);
        var dispatcher = delivery.Services.GetRequiredService<IOutgoingMessageDispatcher>();
        var copyId = copy.Id;
        delivery.AfterCommit.Add(token => dispatcher.DispatchAsync(notices, copyId, token));
    }
}
