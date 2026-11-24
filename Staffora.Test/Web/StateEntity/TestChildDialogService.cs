using Microsoft.EntityFrameworkCore;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Web.StateEntity;

public sealed class TestChildDialogService
{
    public const string NotFound = "The child was not found.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissions;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;

    public TestChildDialogService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissions, WorkflowService workflows, StateHistoryService history)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissions = permissions;
        this.workflows = workflows;
        this.history = history;
    }

    public async Task<Result<DetachedEntityUpdater<TestChild>>> LoadTestChildUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissions.HasPermissionAsync(TestParentDialogService.ViewPermission, cancellationToken))
        {
            return Result<DetachedEntityUpdater<TestChild>>.Failure(TestParentDialogService.NoAccess);
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var child = await context.Set<TestChild>()
            .WithWorkflowGraph()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
        return child is null
            ? Result<DetachedEntityUpdater<TestChild>>.Failure(NotFound)
            : Result<DetachedEntityUpdater<TestChild>>.Success(new DetachedEntityUpdater<TestChild>(child, context));
    }

    public int EditorOptionReads { get; private set; }

    public async Task<Result<IReadOnlyList<TestParent>>> GetTestChildEditorOptionsAsync(CancellationToken cancellationToken)
    {
        EditorOptionReads++;
        if (!await this.permissions.HasPermissionAsync(TestParentDialogService.ViewPermission, cancellationToken))
        {
            return Result<IReadOnlyList<TestParent>>.Failure(TestParentDialogService.NoAccess);
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var parents = await context.Set<TestParent>().AsNoTracking().OrderBy(parent => parent.Name).ThenBy(parent => parent.Id).ToListAsync(cancellationToken);
        return Result<IReadOnlyList<TestParent>>.Success(parents);
    }

    public async Task<Result> UpdateTestChildAndTransitionAsync(DetachedEntityUpdater<TestChild> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        await this.permissions.HasPermissionAsync(TestParentDialogService.ViewPermission, cancellationToken)
            ? await this.workflows.SaveAndTransitionAsync<TestChildState, TestChild>(updater, transitionKey, TestChildWorkflowQueries.WithWorkflowGraph, comment, cancellationToken)
            : Result.Failure(TestParentDialogService.NoAccess);

    public async Task<Result<StateHistoryPage>> GetTestChildHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissions.HasPermissionAsync(TestParentDialogService.ViewPermission, cancellationToken)
            ? await this.history.GetHistoryAsync<TestChild>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(TestParentDialogService.NoAccess);
}
