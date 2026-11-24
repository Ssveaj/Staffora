using System.Linq.Expressions;

namespace Staffora.Core.Workflow.FieldAccess;

public sealed class StateFieldBuilder<TEntity>
    where TEntity : class
{
    private readonly StateFieldConfig<TEntity> config;

    internal StateFieldBuilder(StateFieldConfig<TEntity> config) => this.config = config;

    public StateFieldBuilder<TEntity> Default(FieldAccessFlags flags)
    {
        this.config.SetDefault(flags);
        return this;
    }

    public StateFieldBuilder<TEntity> Field(Expression<Func<TEntity, object?>> field, FieldAccessFlags flags)
    {
        this.config.SetField(MemberPaths.PropertyOf(field).Name, flags);
        return this;
    }

    public StateFieldBuilder<TEntity> Area(string name, FieldAccessFlags flags, Action<AreaFieldBuilder<TEntity>>? fields = null)
    {
        this.config.SetArea(name, flags);
        fields?.Invoke(new AreaFieldBuilder<TEntity>(this.config, name));
        return this;
    }
}

public sealed class AreaFieldBuilder<TEntity>
    where TEntity : class
{
    private readonly StateFieldConfig<TEntity> config;
    private readonly string area;

    internal AreaFieldBuilder(StateFieldConfig<TEntity> config, string area)
    {
        this.config = config;
        this.area = area;
    }

    public AreaFieldBuilder<TEntity> Field(Expression<Func<TEntity, object?>> field)
    {
        this.config.AddToArea(this.area, MemberPaths.PropertyOf(field).Name);
        return this;
    }
}
