using System.Collections;
using System.Reflection;
using Staffora.Core;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public abstract class StateEntityDialogContextBase<TState, TEntity> : StateEntityDialogContext
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    private EntitySnapshot? newEntity;

    private IReadOnlySet<string>? graphPaths;

    private bool lookupsLoaded;

    protected StateEntityDialogContextBase(Guid? entityId, Func<IQueryable<TEntity>, IQueryable<TEntity>> queryConfigurator)
    {
        ArgumentNullException.ThrowIfNull(queryConfigurator);
        if (entityId == Guid.Empty)
        {
            throw new ArgumentException("An entity is opened by its id, or created with null; the empty id names nothing.", nameof(entityId));
        }

        EntityId = entityId;
        QueryConfigurator = queryConfigurator;
    }

    public Func<IQueryable<TEntity>, IQueryable<TEntity>> QueryConfigurator { get; }

    public DetachedEntityUpdater<TEntity>? Updater { get; private set; }

    public TEntity? Entity { get; private set; }

    public Workflow<TState, TEntity> Workflow => WorkflowRegistry.Default.Get<TState, TEntity>();

    public override Type EntityType => typeof(TEntity);

    public override Enum? State => Entity?.State;

    public override bool HasChanges =>
        Updater?.HasChanges()
        ?? (this.newEntity is { } built && Entity is { } entity && !built.Matches(entity));

    internal override object? EntityObject => Entity;

    internal override IWorkflow EntityWorkflow => Workflow;

    private protected override Task<Result<StateHistoryPage>> AskHistoryAsync(Guid entityId, StateHistoryQuery query) =>
        LoadHistoryAsync(entityId, query, Lifetime);

    protected abstract Task<Result<DetachedEntityUpdater<TEntity>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken);

    protected abstract Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<TEntity> updater, string transitionKey, string? comment, CancellationToken cancellationToken);

    protected virtual Task<Result> DeleteAsync(DetachedEntityUpdater<TEntity> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        Workflows.TransitionEntityAsync<TState, TEntity>((Guid)updater.OriginalValue(nameof(IStateEntity.Id))!, transitionKey, QueryConfigurator, comment, cancellationToken);

    protected abstract Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken);

    protected virtual Task<Result<IReadOnlyList<IEntityAttributeDefinition>>> LoadCustomFieldsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure($"The {typeof(TEntity).Name} dialog does not load its custom fields."));

    protected virtual Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken) => Task.FromResult(Result.Success());

    protected virtual bool ReadsEditorOptionsWithEveryLoad => false;

    protected virtual Task<Result<TEntity>> NewEntityAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result<TEntity>.Failure($"A {typeof(TEntity).Name} is not created here."));

    protected virtual Task<Result<Guid>> CreateEntityAsync(TEntity entity, string transitionKey, CancellationToken cancellationToken) =>
        Task.FromResult(Result<Guid>.Failure($"A {typeof(TEntity).Name} is not created here."));

    private protected override async Task<string?> LoadEntityCoreAsync(CancellationToken cancellationToken)
    {
        if (!this.lookupsLoaded)
        {
            var entityLoad = LoadTheEntityAsync(cancellationToken);
            var fieldsLoad = CustomFieldCollection.Of(typeof(TEntity)).Count > 0 ? LoadCustomFieldsAsync(cancellationToken) : null;
            var optionsLoad = LoadEditorOptionsAsync(cancellationToken);
            var viewsLoad = LoadViewAccessAsync(cancellationToken);
            var historyLoad = LoadHistoryAccessAsync(cancellationToken);
            await Task.WhenAll(new Task[] { entityLoad, optionsLoad, viewsLoad, historyLoad }.Concat(fieldsLoad is null ? [] : [fieldsLoad]));

            if (await entityLoad is { } entityReason)
            {
                return entityReason;
            }

            if (fieldsLoad is not null)
            {
                var fields = await fieldsLoad;
                if (!fields.IsSuccess || fields.Value is null)
                {
                    return string.IsNullOrWhiteSpace(fields.Error) ? $"The custom fields of the {typeof(TEntity).Name} could not be read." : fields.Error;
                }

                CustomFields = fields.Value;
            }

            var options = await optionsLoad;
            if (!options.IsSuccess)
            {
                return string.IsNullOrWhiteSpace(options.Error) ? $"The choices of the {typeof(TEntity).Name} could not be read." : options.Error;
            }

            this.lookupsLoaded = true;
            return null;
        }

        if (ReadsEditorOptionsWithEveryLoad)
        {
            var entityLoad = LoadTheEntityAsync(cancellationToken);
            var optionsLoad = LoadEditorOptionsAsync(cancellationToken);
            await Task.WhenAll(entityLoad, optionsLoad);
            if (await entityLoad is { } entityReason)
            {
                return entityReason;
            }

            var options = await optionsLoad;
            return options.IsSuccess ? null : string.IsNullOrWhiteSpace(options.Error) ? $"The choices of the {typeof(TEntity).Name} could not be read." : options.Error;
        }

        return await LoadTheEntityAsync(cancellationToken);
    }

    private async Task<string?> LoadTheEntityAsync(CancellationToken cancellationToken)
    {
        if (EntityId is { } id)
        {
            var loaded = await LoadUpdaterAsync(id, cancellationToken);
            if (!loaded.IsSuccess || loaded.Value is null)
            {
                return string.IsNullOrWhiteSpace(loaded.Error) ? $"The {typeof(TEntity).Name} was not found." : loaded.Error;
            }

            if (loaded.Value.Entity.Id != id)
            {
                Log.LogError("The {EntityType} dialog for {EntityId} was handed the updater of {OtherId}.", typeof(TEntity).Name, id, loaded.Value.Entity.Id);
                return LoadFailed;
            }

            Updater = loaded.Value;
            Entity = loaded.Value.Entity;
            this.newEntity = null;

            this.graphPaths ??= Workflows.GetGraphPaths(QueryConfigurator);
            return null;
        }

        var created = await NewEntityAsync(cancellationToken);
        if (!created.IsSuccess || created.Value is null)
        {
            return string.IsNullOrWhiteSpace(created.Error) ? $"A {typeof(TEntity).Name} could not be started." : created.Error;
        }

        var entity = created.Value;
        if (!((Enum)entity.State).IsEntryState())
        {
            var entries = Enum.GetValues<TState>().Where(state => ((Enum)state).IsEntryState()).ToList();
            if (entries.Count != 1)
            {
                throw new InvalidOperationException($"A new {typeof(TEntity).Name} is {entity.State}, and {typeof(TState).Name} marks {entries.Count} entry states, so the dialog cannot choose one.");
            }

            entity.State = entries[0];
        }

        Entity = entity;
        this.newEntity = EntitySnapshot.Of(entity);
        return null;
    }

    internal override IReadOnlyList<ITransition> CandidateTransitions()
    {
        if (Entity is not { } entity)
        {
            return [];
        }

        var steps = Workflow.GetTransitionsFrom(entity.State).Where(transition => !transition.IsSystemDriven).ToList();
        var createsBySelfTransition = steps.Any(transition => transition.IsSelfTransition);
        return [.. steps
            .Where(transition => !IsCreate || ((transition.IsSelfTransition || !createsBySelfTransition) && transition.CommentPrompt is null))
            .Where(transition => TransitionFilter?.Invoke(transition) ?? true)];
    }

    private protected override async Task<Result<IReadOnlyList<TransitionOffer>>> AskTransitionOffersAsync()
    {
        var candidates = CandidateTransitions();
        if (Entity is not { } entity)
        {
            return Result<IReadOnlyList<TransitionOffer>>.Success([]);
        }

        var answers = EntityId is { } id
            ? await Workflows.GetAvailableTransitionsAsync<TState, TEntity>(id, QueryConfigurator, Lifetime)
            : await Workflows.GetTransitionAccessAsync<TState, TEntity>(entity.State, Lifetime);
        if (!answers.IsSuccess || answers.Value is null)
        {
            return Result<IReadOnlyList<TransitionOffer>>.Failure(answers.Error);
        }

        var byKey = answers.Value.ToDictionary(answer => answer.Transition.Key, StringComparer.Ordinal);

        this.editsRefusal = EntityId is null ? null
            : Workflow.GetEditAuthority(entity.State) is not { } authority ? (Workflow.EditsGoWithItsSteps(entity.State) ? null : TransitionRefusals.NoEditsHere)
            : byKey.TryGetValue(authority.Key, out var saveAnswer) && saveAnswer.MayRun ? null
            : TransitionRefusals.EditsNotYours;
        IReadOnlyList<TransitionOffer> offers = [.. candidates
            .Select(candidate => byKey.TryGetValue(candidate.Key, out var answer) ? answer : null)
            .Where(answer => answer is { MayRun: true })
            .Select(answer => new TransitionOffer(answer!.Transition, answer.CanRun, answer.BlockingReasons))];
        return Result<IReadOnlyList<TransitionOffer>>.Success(offers);
    }

    private TState PersistedState => Updater is { } updater ? (TState)updater.OriginalValue(nameof(IStateEntity<TState>.State))! : Entity!.State;

    private string? editsRefusal;

    private string? EditsRefusalFor(ITransition step) =>
        !IsCreate && HasChanges && this.editsRefusal is { } refusal && !step.IsSave && !((Enum)step.To).IsDeleteState()
        && !ReferenceEquals(Workflow.GetEditAuthority(PersistedState), step)
            ? refusal
            : null;

    protected virtual bool IsLockedRecord(TEntity entity) => false;

    protected virtual bool IsLockedMember(string fieldOrArea) => false;

    private FieldAccessFlags UnlessLocked(FieldAccessFlags access, string member) =>
        Entity is not null && (IsLockedRecord(Entity) || IsLockedMember(member) || !IsUnlocked) ? access & FieldAccessFlags.Visible : access;

    internal override bool StateHasSave => Entity is not null && Workflow.GetEditAuthority(PersistedState) is not null;

    internal override bool IsEditStep(ITransition transition) => Entity is not null && ReferenceEquals(Workflow.GetEditAuthority(PersistedState), transition);

    internal override bool RecordLocked => Entity is not null && IsLockedRecord(Entity);

    internal override FieldAccessFlags FieldAccessOf(string field) =>
        Entity is null ? FieldAccessFlags.None : UnlessLocked(Workflow.GetFieldConfig(PersistedState).GetFieldAccess(field), field);

    internal override FieldAccessFlags AreaAccessOf(string area) =>
        Entity is null ? FieldAccessFlags.None : UnlessLocked(Workflow.GetFieldConfig(PersistedState).GetAreaAccess(area), area);

    internal override FieldAccessFlags RowsAreaAccessOf(string area) =>
        Entity is null ? FieldAccessFlags.None : Workflow.GetFieldConfig(PersistedState).GetAreaAccess(area);

    internal override CustomFieldRequirement RequirementOf(Type definitionType)
    {
        if (Entity is null)
        {
            return CustomFieldRequirement.None;
        }

        var state = PersistedState;
        if (Workflow.RequiresCustomFieldsIn(state, definitionType))
        {
            return CustomFieldRequirement.Required;
        }

        IReadOnlyList<string> before = [.. Workflow.StepsRequiringCustomFieldsFrom(state, definitionType).Select(LabelOf).Distinct(StringComparer.Ordinal)];
        return before.Count == 0 ? CustomFieldRequirement.None : new CustomFieldRequirement(false, before);
    }

    internal override IReadOnlyList<TransitionOffer> JudgedOffers(IReadOnlyList<TransitionOffer> answers)
    {
        if (Entity is not { } entity)
        {
            return [];
        }

        var edited = IsCreate || HasChanges;
        var offers = new List<TransitionOffer>();
        foreach (var answer in answers)
        {
            var offer = answer;

            if (!IsCreate && HasChanges && answer.Transition.TakesNoEdits)
            {
                offers.Add(answer with { CanRun = false, BlockingReasons = answer.CanRun ? [TransitionRefusals.PendingEdits] : [TransitionRefusals.PendingEdits, .. answer.BlockingReasons], WaitsForSave = answer.CanRun });
                continue;
            }

            if (EditsRefusalFor(answer.Transition) is { } notYours)
            {
                offers.Add(answer with { CanRun = false, BlockingReasons = answer.CanRun ? [notYours] : [notYours, .. answer.BlockingReasons], WaitsForSave = answer.CanRun });
                continue;
            }

            if (edited && !entity.IsInTransition && !((Enum)answer.Transition.To).IsDeleteState())
            {
                offer = JudgeInMemory(entity, Step(answer.Transition)) switch
                {
                    { Outcome: JudgementOutcome.Passed } => answer with { CanRun = true, BlockingReasons = [] },
                    { Outcome: JudgementOutcome.Blocked, Reasons: var reasons } => answer with { CanRun = false, BlockingReasons = reasons },

                    _ => answer,
                };
            }

            offers.Add(offer);
        }

        return offers;
    }

    internal override async Task<TransitionAttempt> RunTransitionAsync(ITransition transition, string? comment)
    {
        var step = Step(transition);
        var deletes = ((Enum)step.To).IsDeleteState();
        if (Entity is not { } entity)
        {
            return TransitionAttempt.Refused($"The {typeof(TEntity).Name} is not loaded.");
        }

        if (Updater is { } loaded && (loaded.Entity.Id != EntityId || !ReferenceEquals(loaded.Entity, entity)))
        {
            Log.LogWarning("Refused {TransitionKey} in the {EntityType} dialog for {EntityId}: its entity names another row.", step.Key, typeof(TEntity).Name, EntityId);
            return TransitionAttempt.Refused($"The {typeof(TEntity).Name} in this dialog no longer matches the record it opened. Close the dialog and open it again.");
        }

        if (!IsCreate && HasChanges && step.TakesNoEdits)
        {
            return TransitionAttempt.Refused(TransitionRefusals.PendingEdits);
        }

        if (EditsRefusalFor(step) is { } notYours)
        {
            return TransitionAttempt.Refused(notYours);
        }

        if (!deletes)
        {
            if (OnBeforeTransition is { } check && !await check(step))
            {
                return TransitionAttempt.GivenUp;
            }

            if (IsCreate || HasChanges)
            {
                var judgement = JudgeInMemory(entity, step);
                if (judgement.Outcome == JudgementOutcome.Blocked)
                {
                    return TransitionAttempt.Refused(string.Join(" ", judgement.Reasons));
                }
            }

            if (IsCreate)
            {
                return await CreateWithTheStepAsync(entity, step);
            }
        }

        if (EntityId is not { } id || Updater is not { } updater)
        {
            return TransitionAttempt.Refused($"Save the {typeof(TEntity).Name} before this step.");
        }

        var result = deletes
            ? await DeleteAsync(updater, step.Key, comment, Lifetime)
            : await SaveAndTransitionAsync(updater, step.Key, comment, Lifetime);
        if (result.IsSuccess)
        {
            HasSaved = true;
            if (deletes)
            {
                CloseAsDeleted();
            }
            else
            {
                await ReloadEntityAsync();
            }

            return TransitionAttempt.Succeeded;
        }

        if (!HasChanges)
        {
            await ReloadEntityAsync();
        }

        return TransitionAttempt.Refused(string.IsNullOrWhiteSpace(result.Error) ? "The step could not be completed. Try again." : result.Error);
    }

    private async Task<TransitionAttempt> CreateWithTheStepAsync(TEntity entity, Transition<TState, TEntity> step)
    {
        var created = await CreateEntityAsync(entity, step.Key, Lifetime);
        if (!created.IsSuccess || created.Value == Guid.Empty)
        {
            return TransitionAttempt.Refused(string.IsNullOrWhiteSpace(created.Error) ? $"The {typeof(TEntity).Name} could not be created." : created.Error);
        }

        EntityId = created.Value;
        HasSaved = true;
        await LoadAsync();
        return TransitionAttempt.Succeeded;
    }

    private Transition<TState, TEntity> Step(ITransition transition) =>
        transition as Transition<TState, TEntity> ?? Workflow.GetTransition(transition.Key);

    private Judgement JudgeInMemory(TEntity entity, Transition<TState, TEntity> step)
    {
        var loadState = IsCreate || this.graphPaths is null ? new DialogNavigationLoadState() : new DialogNavigationLoadState(entity, this.graphPaths);
        IReadOnlyList<string> reasons;
        try
        {
            reasons = Workflow.GetBlockingReasons(entity, step.Key, new WorkflowEnvironment(Clock, loadState, Services, customFields: CustomFieldCatalogOf()));
        }
        catch (Exception exception)
        {
            Log.LogWarning(exception, "Judging {TransitionKey} on the {EntityType} in memory failed.", step.Key, typeof(TEntity).Name);
            return new Judgement(JudgementOutcome.Blocked, [CannotBeChecked]);
        }

        if (loadState.MissedAny)
        {
            return new Judgement(JudgementOutcome.LeftToTheServer, []);
        }

        return reasons.Count == 0 ? new Judgement(JudgementOutcome.Passed, []) : new Judgement(JudgementOutcome.Blocked, reasons);
    }

    private CustomFieldCatalog? CustomFieldCatalogOf() =>
        CustomFields is { } fields
            ? new CustomFieldCatalog(CustomFieldCollection.Of(typeof(TEntity)).DistinctBy(collection => collection.DefinitionType)
                .ToDictionary(collection => collection.DefinitionType, collection => (IReadOnlyList<IEntityAttributeDefinition>)[.. fields.Where(collection.DefinitionType.IsInstanceOfType)]))
            : null;

    private enum JudgementOutcome
    {
        Passed,
        Blocked,
        LeftToTheServer,
    }

    private sealed record Judgement(JudgementOutcome Outcome, IReadOnlyList<string> Reasons);

    private sealed class EntitySnapshot
    {
        private readonly List<object?> scalars;
        private readonly List<List<(object Row, List<object?> Scalars)>> collections;

        private EntitySnapshot(List<object?> scalars, List<List<(object Row, List<object?> Scalars)>> collections)
        {
            this.scalars = scalars;
            this.collections = collections;
        }

        public static EntitySnapshot Of(TEntity entity) =>
            new(ScalarValues(entity), [.. collectionProperties.Select(property => RowsOf(property, entity))]);

        public bool Matches(TEntity entity)
        {
            if (!this.scalars.SequenceEqual(ScalarValues(entity)))
            {
                return false;
            }

            for (var index = 0; index < collectionProperties.Count; index++)
            {
                var now = RowsOf(collectionProperties[index], entity);
                var then = this.collections[index];
                if (now.Count != then.Count
                    || now.Zip(then).Any(pair => !ReferenceEquals(pair.First.Row, pair.Second.Row) || !pair.First.Scalars.SequenceEqual(pair.Second.Scalars)))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<(object Row, List<object?> Scalars)> RowsOf(PropertyInfo property, TEntity entity) =>
            property.GetValue(entity) is IEnumerable rows
                ? [.. rows.Cast<object>().Select(row => (row, ScalarValues(row)))]
                : [];

        private static readonly IReadOnlyList<PropertyInfo> collectionProperties = [.. typeof(TEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Where(property => property.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
            .OrderBy(property => property.Name, StringComparer.Ordinal)];
    }

    private static List<object?> ScalarValues(object row) =>
        [.. ScalarPropertiesOf(row.GetType()).Select(property => property.GetValue(row))];

    private static IReadOnlyList<PropertyInfo> ScalarPropertiesOf(Type type) => [.. type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
        .Where(property => IsScalar(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType))
        .OrderBy(property => property.Name, StringComparer.Ordinal)];

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan);
}
