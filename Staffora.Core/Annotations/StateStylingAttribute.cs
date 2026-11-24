namespace Staffora.Core.Annotations;

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class StateStylingAttribute : Attribute
{
    public StateStylingAttribute(string color)
    {
        Color = color;
    }

    public string Color { get; }
}
