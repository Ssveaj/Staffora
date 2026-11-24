using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Messaging;
using Staffora.Services.Messaging;

namespace Staffora.Test.Integration.Infrastructure;

public sealed class CommitProbe
{
    private readonly ConcurrentQueue<CommitRecord> records = new();
    private readonly RealDatabase database;

    public CommitProbe(RealDatabase database)
    {
        this.database = database;
    }

    public IReadOnlyList<CommitRecord> Records => [.. this.records];

    public async Task RecordAsync(string what, Guid parentId, CancellationToken cancellationToken)
    {
        await using var reader = this.database.CreateSystemContext();
        await reader.Database.OpenConnectionAsync(cancellationToken);

        await reader.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 0", cancellationToken);
        try
        {
            var state = await reader.TestParents.AsNoTracking().Where(row => row.Id == parentId).Select(row => (TestParentState?)row.State).SingleOrDefaultAsync(cancellationToken);
            this.records.Enqueue(new CommitRecord(what, parentId, state));
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            this.records.Enqueue(new CommitRecord(what, parentId, CommittedState: null));
        }
    }
}

public sealed record CommitRecord(string What, Guid ParentId, TestParentState? CommittedState);

public sealed class ProbingDispatcher : IOutgoingMessageDispatcher
{
    private readonly ConcurrentQueue<DispatchedBatch> batches = new();
    private readonly CommitProbe probe;

    public ProbingDispatcher(CommitProbe probe)
    {
        this.probe = probe;
    }

    public IReadOnlyList<DispatchedBatch> Batches => [.. this.batches];

    public async Task DispatchAsync(IReadOnlyList<OutgoingMessage> messages, Guid historyEntryId, CancellationToken cancellationToken)
    {
        this.batches.Enqueue(new DispatchedBatch([.. messages], historyEntryId));
        foreach (var message in messages)
        {
            await this.probe.RecordAsync($"dispatch {message.MessageType}", message.EntityId, cancellationToken);
        }
    }
}

public sealed record DispatchedBatch(IReadOnlyList<OutgoingMessage> Messages, Guid HistoryEntryId);
