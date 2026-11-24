namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public sealed class OpenStateEntityDialogs
{
    private readonly List<StateEntityDialogSession> open = [];

    public IReadOnlyList<StateEntityDialogContext> All => [.. this.open.SelectMany(session => session.Targets.Select(target => target.Context))];

    public StateEntityDialogContext? Top => TopSession?.Current.Context;

    internal IReadOnlyList<StateEntityDialogSession> Sessions => this.open;

    internal StateEntityDialogSession? TopSession => this.open.Count == 0 ? null : this.open[^1];

    internal IDisposable Register(StateEntityDialogSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.open.Add(session);
        return new Registration(this, session);
    }

    private sealed class Registration : IDisposable
    {
        private bool disposed;
        private readonly OpenStateEntityDialogs dialogs;
        private readonly StateEntityDialogSession session;

        public Registration(OpenStateEntityDialogs dialogs, StateEntityDialogSession session)
        {
            this.dialogs = dialogs;
            this.session = session;
        }

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.disposed = true;
                this.dialogs.open.Remove(this.session);
            }
        }
    }
}
