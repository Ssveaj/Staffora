using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Data;

namespace Staffora.Services.Workflow;

public sealed partial class WorkflowService
{
    private const string NoUser = "Only a signed-in user runs a workflow step.";

    public const string StepCannotBeChecked = "This step cannot be checked yet.";

    public const string HeldTenant = "The tenant is suspended, scheduled for deletion or archived, so its records are held and no step changes them.";

    public static string SystemDrivenRefusal(string stepName) => $"The step '{stepName}' is taken by the system alone, as a consequence of other changes. No one runs it directly.";

    private static bool IsFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    private Result Refused<TEntity>(Exception exception, Guid entityId, ITransition? transition)
    {
        var transitionKey = transition?.Key;
        var entity = typeof(TEntity).Name;
        var reason = exception switch
        {
            TransitionRefusedException refused => string.Join(" ", refused.Reasons),
            TransitionLockedException => Held<TEntity>(),
            DbUpdateConcurrencyException => $"The {entity} was changed or moved by someone else while the step ran. Reload it and try again.",
            WorkflowFieldEditException field => field.Message,

            SystemStepRefusedException followUp => followUp.Message,

            StepFollowUpRefusedException followUpRefused => followUpRefused.Message,

            OrganizationStructureException structure => structure.Message,

            AppLockBusyException { Lock: var appLock } when appLock == AppLock.CommentThread => $"Someone was writing a comment on the {entity} while the step ran, so nothing was changed. Try again.",

            AppLockBusyException busy => busy.Message,

            TenantHeldException => HeldTenant,

            TenantIsolationException => NotFound<TEntity>(),
            _ => null,
        };

        if (reason is null)
        {
            this.log.LogError(exception, "{TransitionKey} on {EntityType} {EntityId} failed and was rolled back.", transitionKey, entity, entityId);
            return Result.Failure(transitionKey is null
                ? $"The step on the {entity} could not be completed, so nothing was changed."
                : $"The step '{NameOf(transition!)}' could not be completed, so nothing was changed.");
        }

        this.log.LogInformation("{TransitionKey} on {EntityType} {EntityId} was refused and rolled back: {Reason}", transitionKey, entity, entityId, reason);
        return Result.Failure(reason);
    }

    private static string NameOf(ITransition transition) => transition.Label ?? DisplayNameOf(transition.To);

    internal static string? CommentRefusal(ITransition transition, string? comment) =>
        transition.CommentPrompt is null ? null
        : string.IsNullOrWhiteSpace(comment) ? transition.CommentPrompt.Required ? $"The step '{NameOf(transition)}' asks a comment ({transition.CommentPrompt.Title}), and none was given, so nothing was changed." : null
        : comment.Trim().Length > Comment.MaxTextLength ? $"The comment for the step '{NameOf(transition)}' is longer than {Comment.MaxTextLength} characters, so nothing was changed."
        : null;

    private static string DisplayNameOf(Enum value) =>
        value.GetType().GetField(value.ToString())?.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()?.GetName() ?? value.ToString();

    private static string NotFound<TEntity>() => $"The {typeof(TEntity).Name} was not found.";

    private static string Untracked<TEntity>() =>
        $"The {typeof(TEntity).Name} was loaded without change tracking, so no step can run on it. Load it through its workflow graph.";

    private static string Held<TEntity>() =>
        $"The {typeof(TEntity).Name}, or a record its step changes, is being moved by another step right now. Try again when it has finished.";
}
