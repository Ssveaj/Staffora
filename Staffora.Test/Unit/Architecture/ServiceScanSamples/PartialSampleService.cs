using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;

namespace Staffora.Test.Unit.Architecture.ServiceScanSamples;

public sealed partial class PartialSampleService
{
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly StateHistoryService history;

    public PartialSampleService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, StateHistoryService history)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.history = history;
    }

    public async Task<Result> CheckedInFirstPartAsync(CancellationToken cancellationToken)
    {
        if (!await this.MayReadAsync(cancellationToken))
        {
            return Result.Failure("No.");
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return Result.Success();
    }

    private Task<bool> MayReadAsync(CancellationToken cancellationToken) =>
        this.permissionService.HasPermissionAsync(RolePermission.UsersView, cancellationToken);
}
