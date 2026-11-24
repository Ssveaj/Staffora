using Microsoft.AspNetCore.Components;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public interface IStateEntityDialog<TContext> : IComponent
    where TContext : StateEntityDialogContext
{
    TContext Context { get; set; }
}
