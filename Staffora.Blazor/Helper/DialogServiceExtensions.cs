using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Radzen;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;

namespace Staffora.Blazor.Helper;

public static class DialogServiceExtensions
{
    public static async Task<bool> ConfirmAsync(this DialogService dialogService, string message, string title = "Confirm", string okText = "Yes", string cancelText = "Cancel", bool destructive = false, string? icon = null) =>
        await dialogService.OpenFormDialogAsync<ConfirmDialog, bool>(
            title,
            parameters => parameters
                .Set(dialog => dialog.Message, message)
                .Set(dialog => dialog.OkText, okText)
                .Set(dialog => dialog.CancelText, cancelText)
                .Set(dialog => dialog.Destructive, destructive)
                .Set(dialog => dialog.Icon, icon),
            new FormDialogOptions { Width = "460px", CloseOnOverlayClick = true });

    public static Task ShowErrorAsync(this DialogService dialogService, string? message, string title = "Something went wrong") =>
        dialogService.OpenFormDialogAsync<ErrorDialog, bool>(
            title,
            parameters => parameters.Set(dialog => dialog.Message, string.IsNullOrWhiteSpace(message) ? "The step could not be completed." : message),
            new FormDialogOptions { Width = "460px", CloseOnOverlayClick = true });

    public static Task ShowMessageAsync(this DialogService dialogService, string message, string title = "Done", string okText = "OK", string icon = "check_circle", DialogTone tone = DialogTone.Success) =>
        dialogService.OpenFormDialogAsync<MessageDialog, bool>(
            title,
            parameters => parameters
                .Set(dialog => dialog.Message, message)
                .Set(dialog => dialog.OkText, okText)
                .Set(dialog => dialog.Icon, icon)
                .Set(dialog => dialog.Tone, tone),
            new FormDialogOptions { Width = "460px", CloseOnOverlayClick = true });

    public static Task<string?> PromptAsync(this DialogService dialogService, string title, string? message = null, string? placeholder = null, string okText = "OK", string cancelText = "Cancel", bool required = false, string? requiredMessage = null, string? initialText = null, int? maxLength = null) =>
        dialogService.OpenFormDialogAsync<PromptDialog, string>(
            title,
            parameters => parameters
                .Set(dialog => dialog.Message, message)
                .Set(dialog => dialog.Placeholder, placeholder)
                .Set(dialog => dialog.OkText, okText)
                .Set(dialog => dialog.CancelText, cancelText)
                .Set(dialog => dialog.Required, required)
                .Set(dialog => dialog.RequiredMessage, requiredMessage ?? "Write something first.")
                .Set(dialog => dialog.InitialText, initialText)
                .Set(dialog => dialog.MaxLength, maxLength),
            new FormDialogOptions { Width = "520px" });

    public static async Task<bool> OpenFormDialogAsync<TComponent>(this DialogService dialogService, string title, Action<FormDialogParameters<TComponent>>? parameters = null, FormDialogOptions? options = null)
        where TComponent : IComponent =>
        await dialogService.OpenFormDialogAsync<TComponent, bool>(title, parameters, options);

    public static async Task<TResult?> OpenFormDialogAsync<TComponent, TResult>(this DialogService dialogService, string title, Action<FormDialogParameters<TComponent>>? parameters = null, FormDialogOptions? options = null)
        where TComponent : IComponent
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        var builder = new FormDialogParameters<TComponent>();
        parameters?.Invoke(builder);
        options ??= new FormDialogOptions();

        var hostParameters = new Dictionary<string, object?>
        {
            [nameof(FormDialogHost.Title)] = title,
            [nameof(FormDialogHost.Icon)] = options.Icon,
            [nameof(FormDialogHost.ContentType)] = typeof(TComponent),
            [nameof(FormDialogHost.ContentParameters)] = builder.Values,
            [nameof(FormDialogHost.UrlSegment)] = options.UrlSegment,
            [nameof(FormDialogHost.CloseOnEsc)] = options.CloseOnEsc,
        };

        var result = await dialogService.OpenAsync<FormDialogHost>(title, hostParameters, new DialogOptions
        {
            ShowTitle = false,
            AriaLabel = title,
            ShowClose = false,

            AutoFocusFirstElement = false,
            Width = options.Width,
            Height = options.Height,
            Resizable = options.Resizable,
            Draggable = false,
            CloseDialogOnEsc = options.CloseOnEsc,
            CloseDialogOnOverlayClick = options.CloseOnOverlayClick,
            CanClose = options.CanClose,
            CssClass = string.IsNullOrWhiteSpace(options.CssClass) ? "form-dialog-shell" : $"form-dialog-shell {options.CssClass}",

            Style = FrameStyle(options.StateColor),
        });

        return result is TResult value ? value : default;
    }

    private static string? FrameStyle(string? stateColor) =>
        DialogStateColors.For(stateColor) is { } colors
            ? $"--dialog-frame-color: {colors.HeaderFill}; --dialog-header-text: {colors.HeaderText};"
            : null;

    public static Task<StateEntityDialogResult> OpenStateEntityDialogAsync<TBody, TContext>(this DialogService dialogService, TContext context, StateEntityDialogOptions? options = null)
        where TBody : IStateEntityDialog<TContext>
        where TContext : StateEntityDialogContext
    {
        ArgumentNullException.ThrowIfNull(context);
        return dialogService.OpenStateEntityDialogSessionAsync(new StateEntityDialogSession([StateEntityDialogTarget.For<TBody, TContext>(context)]), options);
    }

    public static async Task<StateEntityDialogResult> OpenStateEntityDialogSessionAsync(this DialogService dialogService, StateEntityDialogSession session, StateEntityDialogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(session);
        options ??= new StateEntityDialogOptions();

        if (options.View is { } view)
        {
            session.Current.Context.OpenOnView(view);
        }

        var hostParameters = new Dictionary<string, object?>
        {
            [nameof(StateEntityDialogHost.Session)] = session,
        };

        if (options.Arrival is { } arrival && !await arrival.AdmitAsync(session))
        {
            return session.Result;
        }

        var title = session.Current.Context.Title;
        var closing = dialogService.OpenAsync<StateEntityDialogHost>(title, hostParameters, new DialogOptions
        {
            ShowTitle = false,
            AriaLabel = title,
            ShowClose = false,
            Width = options.OpeningWidth,
            Height = options.Height,
            Draggable = false,
            AutoFocusFirstElement = false,
            CloseDialogOnEsc = true,
            CloseDialogOnOverlayClick = false,
            CanClose = () => session.MayCloseAsync(dialogService),
            CssClass = options.ShellClass,
        });

        options.Arrival?.Opened(session);
        await closing;

        await Task.Yield();
        return session.Result;
    }

    internal static Task<bool> ConfirmDiscardChangesAsync(this DialogService dialogService, StateEntityDialogContext context) =>
        dialogService.ConfirmAsync(context.UnsavedChangesMessage, "Discard changes?", okText: "Discard changes", cancelText: "Keep editing", destructive: true);

    internal const string NotSavedOnLeaving = "These changes aren't saved automatically when you leave. Save them with a step first, or discard them.";

    internal static Task<bool> ConfirmDiscardUnsavableChangesAsync(this DialogService dialogService, IReadOnlyList<string> reasons, bool refused = false)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        var message = refused
            ? $"The changes could not be saved. {string.Join(" ", reasons)} Discard them and go on?"
            : reasons.Count > 0
            ? $"The changes cannot be saved yet. {string.Join(" ", reasons.Select(AsSentence))} Discard them and go on?"
            : NotSavedOnLeaving;
        return dialogService.ConfirmAsync(message, "Discard changes?", okText: "Discard changes", cancelText: "Keep editing", destructive: true);
    }

    private static string AsSentence(string reason)
    {
        var trimmed = reason.TrimEnd();
        return trimmed.EndsWith('.') || trimmed.EndsWith('?') || trimmed.EndsWith('!') ? trimmed : trimmed + ".";
    }

    internal static Task<bool> ConfirmDiscardChangesAsync(this DialogService dialogService, IReadOnlyList<StateEntityDialogContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        return contexts.Count == 1
            ? dialogService.ConfirmDiscardChangesAsync(contexts[0])
            : dialogService.ConfirmAsync($"{contexts.Count} open records have changes that are not saved. Lose them all?", $"Discard changes in {contexts.Count} records?", okText: "Discard changes", cancelText: "Keep editing", destructive: true);
    }
}

public sealed record StateEntityDialogOptions
{
    public const string DefaultWidth = "720px";

    public const string WidensForSideColumnClass = "state-entity-dialog-shell--widens";

    public string? Width { get; init; }

    internal string OpeningWidth => Width ?? DefaultWidth;

    internal string ShellClass =>
        string.Join(' ', new[] { "state-entity-dialog-shell", Width is null ? WidensForSideColumnClass : null, CssClass }.Where(css => !string.IsNullOrWhiteSpace(css)));

    public string? Height { get; init; }

    public string? CssClass { get; init; }

    public string? View { get; init; }

    internal StateEntityDialogArrival? Arrival { get; init; }
}

public sealed record FormDialogOptions
{
    public string Width { get; init; } = "520px";

    public string? Height { get; init; }

    public string? Icon { get; init; }

    public bool CloseOnEsc { get; init; } = true;

    public bool CloseOnOverlayClick { get; init; }

    public bool Resizable { get; init; }

    public string? CssClass { get; init; }

    public string? StateColor { get; init; }

    public DialogStackSegment? UrlSegment { get; init; }

    public Func<Task<bool>>? CanClose { get; init; }
}

public sealed class FormDialogParameters<TComponent> where TComponent : IComponent
{
    internal Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

    public FormDialogParameters<TComponent> Set<TValue>(Expression<Func<TComponent, TValue>> parameter, TValue value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter.Body is not MemberExpression { Member: var member })
        {
            throw new ArgumentException("Name a parameter property of the component, such as dialog => dialog.Message.", nameof(parameter));
        }

        if (value is null)
        {
            Values.Remove(member.Name);
        }
        else
        {
            Values[member.Name] = value;
        }

        return this;
    }
}
