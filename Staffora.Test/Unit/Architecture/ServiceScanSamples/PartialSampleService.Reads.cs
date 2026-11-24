using Staffora.Core.Entities.Jobs;
using Staffora.Core.Models;
using Staffora.Services.Workflow;

namespace Staffora.Test.Unit.Architecture.ServiceScanSamples;

public sealed partial class PartialSampleService
{
    public async Task<Result> CheckedThroughTheFirstPartAsync(CancellationToken cancellationToken)
    {
        if (!await this.MayReadAsync(cancellationToken))
        {
            return Result.Failure("No.");
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return Result.Success();
    }

    public async Task<Result> UncheckedInTheSecondPartAsync(CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await this.MayReadAsync(cancellationToken) ? Result.Success() : Result.Failure("No.");
    }

    public async Task<Result> AsksAnotherServiceBeforeItChecksAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken)
    {
        var page = await this.history.GetHistoryAsync<JobSlot>(id, query, cancellationToken);
        return page.IsSuccess && await this.MayReadAsync(cancellationToken) ? Result.Success() : Result.Failure("No.");
    }

    public Task<Result<StateHistoryPage>> ForwardInTheSecondPartAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.history.GetHistoryAsync<JobSlot>(id, query, cancellationToken);
}
