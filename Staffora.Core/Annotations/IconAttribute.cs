namespace Staffora.Core.Annotations;

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class IconAttribute : Attribute
{
    public IconAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
