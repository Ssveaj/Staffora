namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public abstract class StateEntityDialogTarget
{
    private protected StateEntityDialogTarget()
    {
    }

    public abstract StateEntityDialogContext Context { get; }

    internal abstract Type BodyType { get; }

    public static StateEntityDialogTarget For<TBody, TContext>(TContext context)
        where TBody : IStateEntityDialog<TContext>
        where TContext : StateEntityDialogContext
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Typed(context, typeof(TBody));
    }

    private sealed class Typed : StateEntityDialogTarget
    {
        public Typed(StateEntityDialogContext context, Type bodyType)
        {
            Context = context;
            BodyType = bodyType;
        }

        public override StateEntityDialogContext Context { get; }

        internal override Type BodyType { get; }
    }
}
