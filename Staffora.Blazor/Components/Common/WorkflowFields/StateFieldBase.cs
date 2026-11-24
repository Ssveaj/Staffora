using System.Linq.Expressions;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Blazor.Components.Common.WorkflowFields;

public abstract class StateFieldBase<TValue> : ComponentBase
{
    [CascadingParameter] public StateEntityDialogContext? DialogContext { get; set; }

    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;

    [Parameter] public TValue? Value { get; set; }

    [Parameter] public EventCallback<TValue?> ValueChanged { get; set; }

    [Parameter] public Expression<Func<TValue?>>? ValueExpression { get; set; }

    [Parameter] public string? Name { get; set; }

    [Parameter] public string? Placeholder { get; set; }

    [Parameter] public string? Helper { get; set; }

    protected string? HelperId => string.IsNullOrWhiteSpace(Helper) ? null : StateFieldFrame.HelperIdOf(InputName);

    protected FieldAccessFlags Access { get; private set; }

    protected string FieldName { get; private set; } = string.Empty;

    protected bool IsVisible => Access.HasFlag(FieldAccessFlags.Visible);

    protected bool IsEditable => IsVisible && Access.HasFlag(FieldAccessFlags.Editable);

    protected bool IsRequired => IsEditable && Access.HasFlag(FieldAccessFlags.Mandatory);

    protected string InputName { get; private set; } = string.Empty;

    protected IReadOnlyDictionary<string, object> InputAttributes => WithHelper(IsRequired ? requiredAttributes : noAttributes);

    protected IReadOnlyDictionary<string, object> WithHelper(IReadOnlyDictionary<string, object> attributes) =>
        HelperId is not { } helper ? attributes : new Dictionary<string, object>(attributes) { ["aria-describedby"] = helper };

    [CascadingParameter] public EditContext? EditContext { get; set; }

    protected IReadOnlyDictionary<string, object> TypingAttributes => new Dictionary<string, object>(InputAttributes)
    {
        ["oninput"] = EventCallback.Factory.Create<ChangeEventArgs>(this, TypedAsync),
    };

    private async Task TypedAsync(ChangeEventArgs args)
    {
        if (!IsEditable || ValueExpression is null || (args.Value is not null && args.Value is not TValue))
        {
            return;
        }

        await ValueChanged.InvokeAsync((TValue?)args.Value);
        EditContext?.NotifyFieldChanged(FieldIdentifier.Create(ValueExpression));
    }

    private static readonly IReadOnlyDictionary<string, object> requiredAttributes = new Dictionary<string, object> { ["aria-required"] = "true" };

    private static readonly IReadOnlyDictionary<string, object> noAttributes = new Dictionary<string, object>();

    protected override void OnParametersSet()
    {
        if (DialogContext is null)
        {
            throw new InvalidOperationException($"{GetType().Name} is a field of a state-entity dialog's body; its layout cascades the dialog's context.");
        }

        if (ValueExpression is null)
        {
            throw new InvalidOperationException($"{GetType().Name} '{Label}' is bound with @bind-Value to a property of the dialog's entity.");
        }

        var field = FieldIdentifier.Create(ValueExpression);
        if (!ReferenceEquals(field.Model, DialogContext.EntityObject))
        {
            throw new InvalidOperationException($"{GetType().Name} '{Label}' binds {field.Model.GetType().Name}.{field.FieldName}; a StateField binds a property of the dialog's own {DialogContext.EntityType.Name}, whose workflow decides its access.");
        }

        FieldName = field.FieldName;
        Access = DialogContext.FieldAccessOf(FieldName);
        InputName = Name ?? $"{Kebab(DialogContext.EntityType.Name)}-{Kebab(FieldName)}";
    }

    protected Task SetAsync(TValue? value) => IsEditable ? ValueChanged.InvokeAsync(value) : Task.CompletedTask;

    internal static string Kebab(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var letter = name[index];
            if (char.IsUpper(letter) && index > 0 && !char.IsUpper(name[index - 1]))
            {
                text.Append('-');
            }

            text.Append(char.ToLowerInvariant(letter));
        }

        return text.ToString();
    }
}
