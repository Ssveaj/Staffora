namespace Staffora.Core.Workflow;

public sealed class LockCandidates
{
    private readonly Dictionary<Type, List<IStateEntity>> byType = [];

    private readonly HashSet<IStateEntity> moving = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyDictionary<Type, IReadOnlyList<IStateEntity>> ByEntityType =>
        this.byType.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<IStateEntity>)pair.Value);

    public IEnumerable<IStateEntity> All => this.byType.Values.SelectMany(list => list);

    public int Count => this.byType.Values.Sum(list => list.Count);

    public bool Contains(IStateEntity entity) =>
        this.byType.TryGetValue(entity.GetType(), out var list) && list.Contains(entity, ReferenceEqualityComparer.Instance);

    public void Add(IStateEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var type = entity.GetType();
        if (!this.byType.TryGetValue(type, out var list))
        {
            this.byType[type] = list = [];
        }

        if (!list.Contains(entity, ReferenceEqualityComparer.Instance))
        {
            list.Add(entity);
        }
    }

    public void AddRange(IEnumerable<IStateEntity> entities)
    {
        foreach (var entity in entities)
        {
            Add(entity);
        }
    }

    internal bool BeginMove(IStateEntity entity) => this.moving.Add(entity);

    internal void EndMove(IStateEntity entity) => this.moving.Remove(entity);
}
