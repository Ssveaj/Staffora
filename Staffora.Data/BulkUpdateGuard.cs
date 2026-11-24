using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Workflow;

namespace Staffora.Data;

internal sealed class BulkUpdateGuard : DbCommandInterceptor, IQueryExpressionInterceptor
{
    public static BulkUpdateGuard Instance { get; } = new();

    public static IReadOnlySet<string> ProtectedColumns { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ITenantEntity.TenantId),
        nameof(ITenantEntity.Created),
        nameof(ITenantEntity.CreatedById),
        nameof(ITenantEntity.ImpersonatedById),
    };

    private static readonly IReadOnlySet<string> editorColumns = new HashSet<string>(StringComparer.Ordinal) { nameof(ITenantEntity.Modified), nameof(ITenantEntity.ModifiedById) };

    public static IReadOnlyDictionary<Type, IReadOnlySet<string>> StructureColumns { get; } = new Dictionary<Type, IReadOnlySet<string>>
    {
        [typeof(OrganizationUnit)] = new HashSet<string>(StringComparer.Ordinal) { nameof(OrganizationUnit.ParentId), nameof(OrganizationUnit.UnitType), nameof(OrganizationUnit.State) },
        [typeof(OrganizationUnitUserRole)] = new HashSet<string>(StringComparer.Ordinal) { nameof(OrganizationUnitUserRole.OrganizationUnitId) },
    };

    private BulkUpdateGuard()
    {
    }

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        new BulkStatementScan(eventData.Context as ApplicationDbContext).Visit(queryExpression);
        return queryExpression;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (!IsBulkStatement(eventData, result) || eventData.Context is not ApplicationDbContext context)
        {
            return result;
        }

        TenantPurge.JudgeStatement(context, command);
        return context.JudgeBulkStatementAsync(command, result, synchronously: true, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!IsBulkStatement(eventData, result) || eventData.Context is not ApplicationDbContext context)
        {
            return result;
        }

        TenantPurge.JudgeStatement(context, command);
        return await context.JudgeBulkStatementAsync(command, result, synchronously: false, cancellationToken);
    }

    private static bool IsBulkStatement(CommandEventData eventData, InterceptionResult<int> result) =>
        !result.HasResult && eventData.CommandSource is CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete;

    private sealed class BulkStatementScan : ExpressionVisitor
    {
        private readonly ApplicationDbContext? context;

        public BulkStatementScan(ApplicationDbContext? context)
        {
            this.context = context;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var isUpdate = node.Method.Name == nameof(EntityFrameworkQueryableExtensions.ExecuteUpdate);
            var isDelete = node.Method.Name == nameof(EntityFrameworkQueryableExtensions.ExecuteDelete);
            if (isUpdate || isDelete)
            {
                var statement = isUpdate ? "update" : "delete";
                var target = node.Method.IsGenericMethod ? node.Method.GetGenericArguments()[0] : null;
                var targets = Targets(target);

                if (targets.Any(type => typeof(ImpersonationAudit).IsAssignableFrom(type)))
                {
                    throw RefuseAuditWrite(statement);
                }

                if (isDelete && this.context?.PurgeLease is not null && TenantPurge.IsTagged(node.Arguments[0]))
                {
                    return base.VisitMethodCall(node);
                }

                if (targets.FirstOrDefault(type => typeof(IEntityAttributeDefinition).IsAssignableFrom(type) || typeof(IEntityAttributeValue).IsAssignableFrom(type)) is { } customField
                    && !(isUpdate && IsTransitionLock(node, targets)))
                {
                    throw RefuseCustomFieldWrite(statement, customField.Name);
                }

                if (targets.Any(type => typeof(StateHistoryEntry).IsAssignableFrom(type)))
                {
                    throw RefuseWorkflowRule(statement, nameof(StateHistoryEntry), "state history is written once, with its transition, and never rewritten or removed by a bulk statement.");
                }

                if (targets.FirstOrDefault(type => typeof(Comment).IsAssignableFrom(type) || typeof(CommentContainer).IsAssignableFrom(type)) is { } thread)
                {
                    throw RefuseWorkflowRule(statement, thread.Name, "a comment thread is written once, comment by comment, and never rewritten or removed by a bulk statement (ADR 0010).");
                }

                foreach (var (notice, bookkeeping) in ApplicationDbContext.NoticeBookkeepingColumns.Where(pair => targets.Any(type => type.IsAssignableFrom(pair.Key) || pair.Key.IsAssignableFrom(type))))
                {
                    if (IsIntegrationRow(notice))
                    {
                        throw RefuseWorkflowRule(statement, notice.Name, notice == typeof(IntegrationReceipt)
                            ? "an integration message's receipt is kept as it was written and is never rewritten or removed by a bulk statement."
                            : "an integration message is kept as it was written and is never rewritten or removed by a bulk statement; only the dispatcher writes its delivery, through SaveChanges.");
                    }

                    if (isDelete || bookkeeping.Count == 0)
                    {
                        throw RefuseWorkflowRule(statement, notice.Name, "a notice's rows are kept as they were written and are never rewritten or removed by a bulk statement.");
                    }

                    if (node.Arguments.Skip(1).SelectMany(SetColumns).Where(column => column is null || !(bookkeeping.Contains(column) || editorColumns.Contains(column))).ToList() is [var foreign, ..])
                    {
                        throw RefuseWorkflowRule(statement, notice.Name, $"a bulk update of a notice's row sets only {string.Join(", ", bookkeeping)} and the editor stamp, never {foreign ?? "a column it cannot read"}.");
                    }
                }

                if (isDelete && targets.FirstOrDefault(type => typeof(IStateEntity).IsAssignableFrom(type)) is { } stateEntity)
                {
                    throw RefuseWorkflowRule(statement, stateEntity.Name, "a state entity is deleted only through its workflow (ADR 0016).");
                }

                if (isDelete && targets.Any(type => type.IsAssignableFrom(typeof(OrganizationUnit)) || typeof(OrganizationUnit).IsAssignableFrom(type)))
                {
                    throw RefuseUnitDelete();
                }
            }

            if (isUpdate)
            {
                var target = node.Method.IsGenericMethod ? node.Method.GetGenericArguments()[0] : null;
                var structure = StructureColumnsOf(Targets(target));
                foreach (var column in node.Arguments.Skip(1).SelectMany(SetColumns))
                {
                    if (column is null || ProtectedColumns.Contains(column))
                    {
                        throw RefuseColumn(column);
                    }

                    if (structure.FirstOrDefault(pair => pair.Columns.Contains(column)) is { Entity: not null } shaped)
                    {
                        throw RefuseStructureColumn(shaped.Entity, column);
                    }

                    if (column == nameof(Tenant.ParentTenantId) && Targets(target).Any(type => type.IsAssignableFrom(typeof(Tenant)) || typeof(Tenant).IsAssignableFrom(type)))
                    {
                        throw RefuseColumn(column);
                    }
                }
            }

            return base.VisitMethodCall(node);
        }

        private IReadOnlyList<Type> Targets(Type? target)
        {
            if (target is null)
            {
                return [typeof(ImpersonationAudit), typeof(StateHistoryEntry), typeof(Comment), typeof(CommentContainer), typeof(OutgoingMessage), typeof(StoredMessage), typeof(MessageRecipient), typeof(IntegrationMessage), typeof(IntegrationReceipt), typeof(IStateEntity)];
            }

            var derived = this.context?.Model.FindEntityType(target)?.GetDerivedTypesInclusive().Select(type => type.ClrType) ?? [];
            return [target, .. derived];
        }

        private static List<(Type Entity, IReadOnlySet<string> Columns)> StructureColumnsOf(IReadOnlyList<Type> targets) =>
            StructureColumns
                .Where(pair => targets.Any(type => type.IsAssignableFrom(pair.Key) || pair.Key.IsAssignableFrom(type)))
                .Select(pair => (pair.Key, pair.Value))
                .ToList();

        private static bool IsIntegrationRow(Type entity) => entity == typeof(IntegrationMessage) || entity == typeof(IntegrationReceipt);

        private static bool IsTransitionLock(MethodCallExpression update, IReadOnlyList<Type> targets) =>
            targets.All(type => typeof(IEntityAttributeDefinition).IsAssignableFrom(type))
            && update.Arguments.Skip(1).SelectMany(SetColumns).ToList() is [nameof(IStateEntity.IsInTransition)];

        private static IEnumerable<string?> SetColumns(Expression setters) =>
            setters is NewArrayExpression array
                ? array.Expressions.Select(setter => setter is NewExpression { Arguments.Count: > 0 } pair ? ColumnOf(pair.Arguments[0]) : null)
                : [null];

        private static string? ColumnOf(Expression selector)
        {
            while (selector is UnaryExpression { NodeType: ExpressionType.Quote or ExpressionType.Convert } unary)
            {
                selector = unary.Operand;
            }

            if (selector is not LambdaExpression lambda)
            {
                return null;
            }

            var body = lambda.Body;
            while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            {
                body = convert.Operand;
            }

            return body switch
            {
                MemberExpression member => member.Member.Name,
                MethodCallExpression { Method.Name: nameof(EF.Property), Arguments: [_, ConstantExpression { Value: string name }] } => name,
                _ => null,
            };
        }

        private TenantIsolationException RefuseColumn(string? column)
        {
            var reason = column switch
            {
                null => "a column it sets cannot be read, so it might set TenantId or an insert-only audit column.",
                nameof(ITenantEntity.TenantId) => "it sets TenantId, and a row never moves to another tenant.",
                nameof(Tenant.ParentTenantId) => "it sets ParentTenantId, a managed tenant's staffing company, which is named as the tenant is created and never changes.",
                _ => $"it sets {column}, which only an insert writes.",
            };

            return this.context is null ? new TenantIsolationException($"Refused a bulk update: {reason}") : this.context.RefuseBulkUpdate(reason);
        }

        private OrganizationStructureException RefuseStructureColumn(Type entity, string column) =>
            this.context is null
                ? new OrganizationStructureException(ApplicationDbContext.BulkStructureWriteReason(entity.Name, column))
                : this.context.RefuseBulkStructureWrite(entity.Name, column);

        private OrganizationStructureException RefuseUnitDelete() =>
            this.context is null ? new OrganizationStructureException(ApplicationDbContext.BulkUnitDeleteReason) : this.context.RefuseBulkUnitDelete();

        private TenantIsolationException RefuseAuditWrite(string statement)
        {
            const string reason = "the impersonation audit is append-only; a row is only inserted, and closed once through SaveChanges.";
            return this.context is null ? new TenantIsolationException($"Refused a bulk {statement} of {nameof(ImpersonationAudit)}: {reason}") : this.context.RefuseBulkAuditWrite(statement, reason);
        }

        private TenantIsolationException RefuseCustomFieldWrite(string statement, string entity)
        {
            const string reason = "custom fields and their values are written only through SaveChanges, which keeps a tenant to its own fields and checks every value.";
            return this.context is null ? new TenantIsolationException($"Refused a bulk {statement} of {entity}: {reason}") : this.context.RefuseBulkCustomFieldWrite(statement, entity, reason);
        }

        private WorkflowFieldEditException RefuseWorkflowRule(string statement, string entity, string reason) =>
            this.context is null
                ? new WorkflowFieldEditException(entity, string.Empty, null, [], $"Refused a bulk {statement} of {entity}: {reason}")
                : this.context.RefuseBulkWorkflowWrite(statement, entity, reason);
    }
}
