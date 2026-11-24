using System.Linq.Expressions;

namespace Staffora.Core.Workflow.FieldAccess;

public sealed class StateFieldConfig<TEntity>
    where TEntity : class
{
    private readonly Dictionary<string, FieldAccessFlags> fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldAccessFlags> areas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> areaOfField = new(StringComparer.Ordinal);

    public FieldAccessFlags DefaultAccess { get; private set; } = FieldAccessFlags.ReadWrite;

    public string? GetArea(string field) => this.areaOfField.GetValueOrDefault(field);

    public bool Declares(string field) => this.fields.ContainsKey(field) || this.areaOfField.ContainsKey(field);

    internal bool Overrides(string field) => this.fields.ContainsKey(field);

    public FieldAccessFlags GetFieldAccess(string field)
    {
        if (this.fields.TryGetValue(field, out var own))
        {
            return own;
        }

        if (this.areaOfField.TryGetValue(field, out var area))
        {
            return this.areas[area];
        }

        return DefaultAccess;
    }

    public FieldAccessFlags GetFieldAccess(Expression<Func<TEntity, object?>> field) =>
        GetFieldAccess(MemberPaths.PropertyOf(field).Name);

    public FieldAccessFlags GetAreaAccess(string area) =>
        this.areas.TryGetValue(area, out var flags) ? flags : DefaultAccess;

    public bool IsVisible(Expression<Func<TEntity, object?>> field) => GetFieldAccess(field).HasFlag(FieldAccessFlags.Visible);

    public bool IsEditable(Expression<Func<TEntity, object?>> field) => GetFieldAccess(field).HasFlag(FieldAccessFlags.Editable);

    public bool IsMandatory(Expression<Func<TEntity, object?>> field) => GetFieldAccess(field).HasFlag(FieldAccessFlags.Mandatory);

    internal void SetDefault(FieldAccessFlags flags) => DefaultAccess = flags;

    internal void SetField(string field, FieldAccessFlags flags)
    {
        if (!this.fields.TryAdd(field, flags))
        {
            throw new WorkflowDefinitionException($"Field '{field}' of {typeof(TEntity).Name} is configured twice in one state.");
        }
    }

    internal void SetArea(string area, FieldAccessFlags flags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        if (!this.areas.TryAdd(area, flags))
        {
            throw new WorkflowDefinitionException($"Area '{area}' of {typeof(TEntity).Name} is configured twice in one state.");
        }
    }

    internal void AddToArea(string area, string field)
    {
        if (this.areaOfField.TryGetValue(field, out var existing) && existing != area)
        {
            throw new WorkflowDefinitionException($"Field '{field}' of {typeof(TEntity).Name} is in both area '{existing}' and area '{area}'.");
        }

        this.areaOfField[field] = area;
    }
}
