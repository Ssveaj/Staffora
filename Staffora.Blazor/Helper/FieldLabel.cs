namespace Staffora.Blazor.Helper;

public static class FieldLabel
{
    public static IReadOnlyDictionary<string, object> Of(string label) => new Dictionary<string, object> { ["aria-label"] = label };

    public static IReadOnlyDictionary<string, object> CheckBoxInput { get; } = new Dictionary<string, object> { ["inert"] = true };
}
