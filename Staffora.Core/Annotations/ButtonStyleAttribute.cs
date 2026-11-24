namespace Staffora.Core.Annotations;

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ButtonStyleAttribute : Attribute
{
    public ButtonStyleAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
