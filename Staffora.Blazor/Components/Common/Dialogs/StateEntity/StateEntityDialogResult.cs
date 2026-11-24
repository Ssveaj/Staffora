namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public sealed record StateEntityDialogResult(bool Changed, bool Deleted, Guid? EntityId);
