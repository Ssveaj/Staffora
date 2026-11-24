namespace Staffora.Core.Annotations;

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class EntityStateCategoryAttribute : Attribute
{
    public EntityStateCategoryAttribute(StateCategory category)
    {
        Category = category;
    }

    public StateCategory Category { get; }
}
