using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Comments;
using Staffora.Services.CustomFields;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Web.StateEntity;

public sealed class TestParentDialogService
{
    public const RolePermission ViewPermission = RolePermission.SettingsView;

    public const string NotFound = "The parent was not found.";

    public const string NoAccess = "You do not have permission to open parents.";

    public const string LoadTag = "Load TestParent updater";

    public const string CreatedMessage = "The parent was created; this note shows once.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissions;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly CustomFieldService attributes;
    private readonly CommentService comments;

    public TestParentDialogService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissions, WorkflowService workflows, StateHistoryService history, CustomFieldService attributes, CommentService comments)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissions = permissions;
        this.workflows = workflows;
        this.history = history;
        this.attributes = attributes;
        this.comments = comments;
    }

    public string? LoadThrows { get; set; }

    public string? SaveThrows { get; set; }

    public string? HistoryThrows { get; set; }

    public Func<Task>? AfterHistoryRead { get; set; }

    public IReadOnlyList<string> NewLines { get; set; } = [];

    public async Task<Result<DetachedEntityUpdater<TestParent>>> LoadTestParentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissions.HasPermissionAsync(ViewPermission, cancellationToken))
        {
            return Result<DetachedEntityUpdater<TestParent>>.Failure(NoAccess);
        }

        if (LoadThrows is { } loadFailure)
        {
            throw new InvalidOperationException(loadFailure);
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var parent = await context.Set<TestParent>()
            .TagWith(LoadTag)
            .WithWorkflowGraph()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
        return parent is null
            ? Result<DetachedEntityUpdater<TestParent>>.Failure(NotFound)
            : Result<DetachedEntityUpdater<TestParent>>.Success(new DetachedEntityUpdater<TestParent>(parent, context, new DetachedEntityUpdaterOptions<TestParent>().WithCollection(row => row.Attributes)));
    }

    public async Task<Result<IReadOnlyList<IEntityAttributeDefinition>>> GetTestParentCustomFieldsAsync(CancellationToken cancellationToken)
    {
        CustomFieldReads++;
        if (!await this.permissions.HasPermissionAsync(ViewPermission, cancellationToken))
        {
            return Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(NoAccess);
        }

        if (CustomFieldsThrow is { } failure)
        {
            throw new InvalidOperationException(failure);
        }

        var fields = await this.attributes.GetDefinitionsAsync<TestParentAttributeDefinition>();
        return fields.IsSuccess && fields.Value is { } read
            ? Result<IReadOnlyList<IEntityAttributeDefinition>>.Success([.. read])
            : Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(fields.Error);
    }

    public int CustomFieldReads { get; private set; }

    public string? CustomFieldsThrow { get; set; }

    public async Task<Result<TestParent>> NewTestParentAsync(CancellationToken cancellationToken) =>
        await this.permissions.HasPermissionAsync(ViewPermission, cancellationToken)
            ? Result<TestParent>.Success(new TestParent { Lines = [.. NewLines.Select(code => new TestParentLine { Code = code, Text = code })] })
            : Result<TestParent>.Failure(NoAccess);

    public async Task<Result<Guid>> CreateTestParentAsync(TestParent parent, string transitionKey, CancellationToken cancellationToken)
    {
        if (!await this.permissions.HasPermissionAsync(ViewPermission, cancellationToken))
        {
            return Result<Guid>.Failure(NoAccess);
        }

        var row = new TestParent { Name = parent.Name.Trim(), Notes = parent.Notes, Priority = parent.Priority, StartsOn = parent.StartsOn, State = parent.State };

        foreach (var value in parent.Attributes.Where(value => !AttributeValues.IsBlank(value)))
        {
            var copy = new TestParentAttribute { EntityAttributeDefinitionId = value.EntityAttributeDefinitionId };
            AttributeValues.CopyValues(value, copy);
            row.Attributes.Add(copy);
        }

        var created = await this.workflows.CreateAndTransitionAsync<TestParentState, TestParent>(row, transitionKey, TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        return created.IsSuccess ? Result<Guid>.Success(created.Value, CreatedMessage) : created;
    }

    public async Task<Result> UpdateTestParentAndTransitionAsync(DetachedEntityUpdater<TestParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        if (SaveThrows is { } saveFailure)
        {
            throw new InvalidOperationException(saveFailure);
        }

        return await this.permissions.HasPermissionAsync(ViewPermission, cancellationToken)
            ? await this.workflows.SaveAndTransitionAsync<TestParentState, TestParent>(updater, transitionKey, TestParentWorkflowQueries.WithWorkflowGraph, comment, cancellationToken)
            : Result.Failure(NoAccess);
    }

    public async Task<Result<StateHistoryPage>> GetTestParentHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken)
    {
        if (!await this.permissions.HasPermissionAsync(ViewPermission, cancellationToken))
        {
            return Result<StateHistoryPage>.Failure(NoAccess);
        }

        if (HistoryThrows is { } historyFailure)
        {
            throw new InvalidOperationException(historyFailure);
        }

        var page = await this.history.GetHistoryAsync<TestParent>(id, query, cancellationToken);
        if (AfterHistoryRead is { } hold)
        {
            await hold();
        }

        return page;
    }

    public const RolePermission ChildrenPermission = RolePermission.OrganizationUnitsView;

    public const string NoChildrenAccess = "You do not have permission to see a parent's children.";

    public int ChildQueries { get; private set; }

    public Func<CancellationToken, Task>? BeforeChildQuery { get; set; }

    public List<CancellationToken> ChildQueryTokens { get; } = [];

    public async Task<Result<ListPage<TestChildRow>>> QueryTestParentChildrenAsync(Guid parentId, ListQuery query, CancellationToken cancellationToken)
    {
        ChildQueries++;
        ChildQueryTokens.Add(cancellationToken);
        if (!await this.permissions.HasPermissionAsync(ChildrenPermission, cancellationToken))
        {
            return Result<ListPage<TestChildRow>>.Failure(NoChildrenAccess);
        }

        if (BeforeChildQuery is { } hold)
        {
            await hold(cancellationToken);
        }

        var list = TestChildList.Definition.Normalize(query);
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var children = context.Set<TestChild>().AsNoTracking().Where(child => child.ParentId == parentId);
        if (list.Search is { Length: > 0 } search)
        {
            children = children.Where(child => child.Name.Contains(search));
        }

        var total = await children.CountAsync(cancellationToken);
        var page = ListPage<TestChildRow>.PageWithin(list, total);
        var rows = await new ListOrder<TestChild>(children).By(child => child.Name, list.Descending).By(child => child.Id).Query
            .Skip((page - 1) * list.PageSize)
            .Take(list.PageSize)
            .Select(child => new TestChildRow(child.Id, child.Name, child.State, child.IsPinned))
            .ToListAsync(cancellationToken);
        return Result<ListPage<TestChildRow>>.Success(ListPage<TestChildRow>.Ungrouped(rows, total, page, list.PageSize));
    }

    public const RolePermission InternalThreadPermission = RolePermission.TenantView;

    public const RolePermission StaffThreadPermission = RolePermission.RolesView;

    public const RolePermission StaffPostPermission = RolePermission.RolesEdit;

    public static readonly IReadOnlyList<CommentThreadAccess> CommentAccess =
    [
        new(CommentAudience.Internal, InternalThreadPermission, InternalThreadPermission),
        new(CommentAudience.Staff, StaffThreadPermission, StaffPostPermission),
    ];

    public string? CommentsThrow { get; set; }

    public int CommentReads { get; private set; }

    public Task<Result<IReadOnlyList<CommentThread>>> GetTestParentCommentsAsync(Guid id, CancellationToken cancellationToken)
    {
        CommentReads++;
        return CommentsThrow is { } failure
            ? throw new InvalidOperationException(failure)
            : this.comments.GetThreadsAsync<TestParent>(id, CommentAccess, cancellationToken);
    }

    public Task<Result<CommentThread>> AddTestParentCommentAsync(Guid id, CommentAudience audience, string text, CancellationToken cancellationToken) =>
        CommentAccess.FirstOrDefault(access => access.Audience == audience) is { } thread
            ? this.comments.AddCommentAsync<TestParent>(id, thread, text, cancellationToken)
            : Task.FromResult(Result<CommentThread>.Failure(CommentService.NoAccess));

    public async Task<Result> UpdateTestParentAsync(DetachedEntityUpdater<TestParent> updater, CancellationToken cancellationToken)
    {
        if (SaveThrows is { } saveFailure)
        {
            throw new InvalidOperationException(saveFailure);
        }

        return await this.permissions.HasPermissionAsync(ViewPermission, cancellationToken)
            ? await this.workflows.SaveEntityAsync(updater, cancellationToken)
            : Result.Failure(NoAccess);
    }
}
