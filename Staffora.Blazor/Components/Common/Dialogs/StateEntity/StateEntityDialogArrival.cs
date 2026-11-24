using Radzen;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

internal sealed class StateEntityDialogArrival
{
    private readonly TaskCompletionSource<StateEntityDialogSession?> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WorkflowService workflows;
    private readonly TimeProvider clock;
    private readonly IServiceProvider services;
    private readonly ILogger log;
    private readonly DialogService dialogs;
    private readonly CancellationToken cancellation;

    public StateEntityDialogArrival(WorkflowService workflows, TimeProvider clock, IServiceProvider services, ILogger log, DialogService dialogs, CancellationToken cancellation)
    {
        this.workflows = workflows;
        this.clock = clock;
        this.services = services;
        this.log = log;
        this.dialogs = dialogs;
        this.cancellation = cancellation;
    }

    public bool WasConsulted { get; private set; }

    public Task<StateEntityDialogSession?> Outcome => this.outcome.Task;

    public async Task<bool> AdmitAsync(StateEntityDialogSession session)
    {
        WasConsulted = true;
        var context = session.Current.Context;

        if (session.Targets.Count != 1 || context.IsCreate || this.cancellation.IsCancellationRequested)
        {
            return Refuse(context);
        }

        context.Attach(this.workflows, this.clock, this.services, this.log, this.dialogs);
        try
        {
            await context.EnsureLoadedAsync().WaitAsync(this.cancellation);
        }
        catch (Exception) when (this.cancellation.IsCancellationRequested)
        {
            return Refuse(context);
        }

        if (context.LoadState != StateEntityDialogLoadState.Loaded || this.cancellation.IsCancellationRequested)
        {
            return Refuse(context);
        }

        return true;
    }

    public void Opened(StateEntityDialogSession session) => this.outcome.TrySetResult(session);

    public void Abandon() => this.outcome.TrySetResult(null);

    private bool Refuse(StateEntityDialogContext context)
    {
        context.End();
        this.outcome.TrySetResult(null);
        return false;
    }
}
