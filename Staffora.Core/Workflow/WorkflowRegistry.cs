using System.Collections.Concurrent;
using System.Reflection;

namespace Staffora.Core.Workflow;

public sealed class WorkflowRegistry
{
    private readonly ConcurrentDictionary<Type, Lazy<IWorkflow>> definitions = new();
    private readonly ConcurrentDictionary<Type, Lazy<IWorkflow>> registered = new();

    public static WorkflowRegistry Default { get; } = new();

    public IWorkflow Get(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return this.registered.GetOrAdd(entityType, type => new Lazy<IWorkflow>(() => Register(type))).Value;
    }

    public Workflow<TState, TEntity> Get<TState, TEntity>()
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState> =>
        Get(typeof(TEntity)) as Workflow<TState, TEntity>
            ?? throw new WorkflowDefinitionException($"The workflow of {typeof(TEntity).Name} is not a Workflow<{typeof(TState).Name}, {typeof(TEntity).Name}>.");

    public static bool HasWorkflow(Type entityType) => entityType.GetCustomAttribute<WorkflowAttribute>(inherit: false) is not null;

    internal IWorkflow GetDefinition(Type entityType) =>
        this.definitions.GetOrAdd(entityType, type => new Lazy<IWorkflow>(() => Build(type))).Value;

    private IWorkflow Register(Type entityType)
    {
        var workflow = GetDefinition(entityType);
        ((IRegistrable)workflow).CompleteRegistration(this);
        return workflow;
    }

    private static IWorkflow Build(Type entityType)
    {
        var attribute = entityType.GetCustomAttribute<WorkflowAttribute>(inherit: false)
            ?? throw new WorkflowDefinitionException($"{entityType.Name} declares no workflow attribute.");

        var workflow = attribute.CreateWorkflow();
        if (workflow is not IRegistrable || workflow.EntityType != entityType)
        {
            throw new WorkflowDefinitionException($"{attribute.GetType().Name} on {entityType.Name} builds a workflow for {workflow.EntityType.Name}.");
        }

        return workflow;
    }
}
