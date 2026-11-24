using Microsoft.Extensions.Logging;

namespace Staffora.Services.Workflow;

public sealed class AfterCommitWork
{
    private readonly List<Func<CancellationToken, Task>> pieces = [];

    public int Count => this.pieces.Count;

    public void Add(Func<CancellationToken, Task> piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        this.pieces.Add(piece);
    }

    public async Task RunAsync(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var due = this.pieces.ToList();
        this.pieces.Clear();
        foreach (var piece in due)
        {
            try
            {
                await piece(CancellationToken.None);
            }
            catch (Exception exception)
            {
                log.LogError(exception, "Work a committed write left for after its commit failed; the write stands.");
            }
        }
    }
}
