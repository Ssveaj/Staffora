using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core;
using Staffora.Core.Annotations;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;
using Staffora.Data;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Unit.Data;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class DeleteCoverageTests
{
    private static readonly IModel model = BuildModel();

    [Fact]
    public void Every_foreign_key_into_an_entity_with_a_delete_state_is_covered()
    {
        var keys = KeysIntoDeletableEntities(model);

        Assert.Superset(
            new HashSet<string> { "TestChild.ParentId -> TestParent", "TestGrandchild.ChildId -> TestChild", "TestParentLine.ParentId -> TestParent", "TestDocument.FolderId -> TestFolder" },
            keys.Select(key => key.ToString()).ToHashSet());
        Assert.Superset(ModelUnderTest.Model.GetEntityTypes().Select(entityType => entityType.ClrType).ToHashSet(), model.GetEntityTypes().Select(entityType => entityType.ClrType).ToHashSet());
        Assert.Empty(Uncovered(keys, RegisteredCleanups(), RegisteredBlockers()));

        Assert.Superset(
            new HashSet<string> { "OrganizationUnitUserRole.OrganizationUnitId -> OrganizationUnit", "OrganizationUnitUserRole.TenantRoleId -> TenantRole", "ResourceUnitMembership.OrganizationUnitId -> OrganizationUnit", "OrganizationUnit.ParentId -> OrganizationUnit", "TenantRolePermission.TenantRoleId -> TenantRole", "Customer.DefaultUnitId -> OrganizationUnit", "CustomerContact.CustomerId -> Customer", "Job.CustomerId -> Customer", "Job.ContactId -> CustomerContact", "Job.OrganizationUnitId -> OrganizationUnit", "JobTimeSlot.JobId -> Job", "JobSlotQualification.JobSlotId -> JobSlot" },
            keys.Select(key => key.ToString()).ToHashSet());
        Assert.Equal(
            ["Comment.AuthorContactId -> CustomerContact", "Job.ContactId -> CustomerContact", "Job.CustomerId -> Customer"],
            Uncovered(keys.Where(key => key.Principal == typeof(Staffora.Core.Entities.Customers.Customer) || key.Principal == typeof(Staffora.Core.Entities.Customers.CustomerContact)), cleanups: [], blockers: []));
        Assert.Equal(
            ["Customer.DefaultUnitId -> OrganizationUnit", "Job.OrganizationUnitId -> OrganizationUnit", "OrganizationUnitUserRole.OrganizationUnitId -> OrganizationUnit", "OrganizationUnitUserRole.TenantRoleId -> TenantRole", "ResourceUnitMembership.OrganizationUnitId -> OrganizationUnit", "ClosedDay.OrganizationUnitId -> OrganizationUnit"],
            Uncovered(keys.Where(key => key.Principal == typeof(Staffora.Core.Entities.Organization.OrganizationUnit) || key.Principal == typeof(Staffora.Core.Entities.Tenancy.TenantRole)), RegisteredCleanups(), blockers: []));
    }

    [Fact]
    public void A_registered_blocker_covers_the_dependents_it_names_and_no_other()
    {
        ForeignKeyInto[] keys =
        [
            new(typeof(Booking), "OtherId", typeof(CoverageSample), Navigation: null, OwnedCascade: false),
            new(typeof(Remark), "OtherId", typeof(CoverageSample), Navigation: null, OwnedCascade: false),
        ];

        Assert.Equal(["Remark.OtherId -> CoverageSample"], Uncovered(keys, cleanups: [], blockers: [new SampleBlocker(typeof(CoverageSample), [typeof(Booking)])]));
        Assert.Equal(["Booking.OtherId -> CoverageSample", "Remark.OtherId -> CoverageSample"], Uncovered(keys, cleanups: [], blockers: [new SampleBlocker(typeof(Booking), [typeof(Booking)])]));
    }

    [Fact]
    public void A_cascade_that_leaves_a_child_state_alone_or_ends_short_of_a_delete_state_covers_nothing()
    {
        Assert.True(CascadeCovers(new SampleCascade(CoverageState.Deleted, LeaveAlone: [])));
        Assert.False(CascadeCovers(new SampleCascade(CoverageState.Deleted, LeaveAlone: [CoverageState.Draft])));
        Assert.False(CascadeCovers(new SampleCascade(CoverageState.Draft, LeaveAlone: [])));
    }

    [Fact]
    public void The_check_finds_a_foreign_key_that_only_a_missing_cleanup_would_cover()
    {
        Assert.Equal(["TestParentLine.ParentId -> TestParent"], Uncovered(KeysIntoDeletableEntities(model), cleanups: [], RegisteredBlockers()));
    }

    [Fact]
    public void The_check_accepts_an_owned_cascade_a_declared_cascade_and_a_condition_on_every_delete_and_nothing_less()
    {
        ForeignKeyInto[] keys =
        [
            new(typeof(OwnedRow), "OwnerId", typeof(CoverageSample), Navigation: null, OwnedCascade: true),
            new(typeof(SampleChild), "ParentId", typeof(SampleParent), nameof(SampleParent.Children), OwnedCascade: false),
            new(typeof(Booking), "SampleId", typeof(CoverageSample), nameof(CoverageSample.Bookings), OwnedCascade: false),
            new(typeof(Remark), "SampleId", typeof(CoverageSample), nameof(CoverageSample.Remarks), OwnedCascade: false),
            new(typeof(Booking), "OtherId", typeof(CoverageSample), Navigation: null, OwnedCascade: false),
        ];

        Assert.Equal(["Remark.SampleId -> CoverageSample", "Booking.OtherId -> CoverageSample"], Uncovered(keys, cleanups: [], blockers: []));
    }

    private static List<ForeignKeyInto> KeysIntoDeletableEntities(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetDeclaredForeignKeys())
            .Where(foreignKey => WorkflowRegistry.HasWorkflow(foreignKey.PrincipalEntityType.ClrType)
                && WorkflowRegistry.Default.Get(foreignKey.PrincipalEntityType.ClrType).States.Any(state => state.IsDeleteState()))
            .Select(foreignKey => new ForeignKeyInto(
                foreignKey.DeclaringEntityType.ClrType,
                string.Join(",", foreignKey.Properties.Select(property => property.Name)),
                foreignKey.PrincipalEntityType.ClrType,
                foreignKey.PrincipalToDependent?.Name,
                foreignKey.DeleteBehavior == DeleteBehavior.Cascade && !typeof(IStateEntity).IsAssignableFrom(foreignKey.DeclaringEntityType.ClrType)))
            .ToList();

    private static List<string> Uncovered(IEnumerable<ForeignKeyInto> keys, IReadOnlyCollection<IDeleteCleanup> cleanups, IReadOnlyCollection<IDeleteBlocker> blockers)
    {
        var uncovered = new List<string>();
        foreach (var key in keys)
        {
            var workflow = WorkflowRegistry.Default.Get(key.Principal);
            var deletes = workflow.Transitions.Where(transition => transition.To.IsDeleteState()).ToList();
            var covered = key.OwnedCascade
                || cleanups.Any(cleanup => cleanup.EntityType.IsAssignableFrom(key.Principal) && cleanup.Dependents.Contains(key.Dependent))
                || blockers.Any(blocker => blocker.EntityType.IsAssignableFrom(key.Principal) && blocker.Dependents.Contains(key.Dependent))
                || (key.Navigation is { } navigation && deletes.All(delete => Cascades(workflow, delete, navigation) || ConditionReads(delete).Any(path => path.Split('.')[0] == navigation)));
            if (!covered)
            {
                uncovered.Add(key.ToString());
            }
        }

        return uncovered;
    }

    private static bool Cascades(IWorkflow workflow, ITransition transition, string navigation) =>
        workflow.GetCascades(transition.Key).Any(cascade => cascade.Navigation == navigation && CascadeCovers(cascade));

    private static bool CascadeCovers(ICascadeAction cascade) => cascade.Target.IsDeleteState() && cascade.LeaveAlone.Count == 0;

    private static IEnumerable<string> ConditionReads(ITransition transition) =>
        ((IEnumerable)transition.GetType().GetProperty("Conditions")!.GetValue(transition)!)
            .Cast<object>()
            .SelectMany(condition => (IEnumerable<string>)condition.GetType().GetProperty("Reads")!.GetValue(condition)!);

    private static List<IDeleteCleanup> RegisteredCleanups()
    {
        using var provider = new ServiceCollection().AddLogging().AddWorkflowServices().AddTestDeleteCleanups().BuildServiceProvider();
        return [.. provider.GetServices<IDeleteCleanup>()];
    }

    private static List<IDeleteBlocker> RegisteredBlockers()
    {
        var services = new ServiceCollection().AddLogging().AddWorkflowServices();
        return [.. services.Where(service => service.ServiceType == typeof(IDeleteBlocker)).Select(service => (IDeleteBlocker)Activator.CreateInstance(service.ImplementationType!)!)];
    }

    private sealed record SampleBlocker(Type EntityType, IReadOnlyList<Type> Dependents) : IDeleteBlocker
    {
        public Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private static IModel BuildModel()
    {
        using var context = new TestDataContext(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection));
        return context.GetService<IDesignTimeModel>().Model;
    }

    private sealed record SampleCascade(Enum Target, IReadOnlyList<Enum> LeaveAlone) : ICascadeAction
    {
        public string Navigation => nameof(CoverageSample.Bookings);

        public Type ChildType => typeof(Booking);
    }

    private sealed record ForeignKeyInto(Type Dependent, string Columns, Type Principal, string? Navigation, bool OwnedCascade)
    {
        public override string ToString() => $"{Dependent.Name}.{Columns} -> {Principal.Name}";
    }

    public enum CoverageState
    {
        Draft = 0,

        [DeleteState]
        Deleted = 1,
    }

    [CoverageSampleWorkflow]
    public sealed class CoverageSample : TenantStateEntityBase<CoverageState>
    {
        public List<Booking> Bookings { get; set; } = [];

        public List<Remark> Remarks { get; set; } = [];
    }

    public sealed class Booking;

    public sealed class Remark;

    public sealed class OwnedRow;

    public sealed class CoverageSampleWorkflowAttribute : WorkflowAttribute
    {
        public override IWorkflow CreateWorkflow()
        {
            var workflow = new Workflow<CoverageState, CoverageSample>();
            workflow.AddState(CoverageState.Draft);
            workflow.AddState(CoverageState.Deleted);
            workflow.AddTransition(new("coverage-delete", CoverageState.Draft, CoverageState.Deleted) { Description = "Delete the sample" })
                .AddCondition(new DelegateCondition<CoverageState, CoverageSample>("No booking refers to it", e => e.Bookings.Count == 0, nameof(CoverageSample.Bookings)))
                .AddCondition(new DelegateCondition<CoverageState, CoverageSample>("No remark refers to it", e => e.Remarks.Count == 0, nameof(CoverageSample.Remarks)));
            workflow.AddTransition(new("coverage-purge", CoverageState.Draft, CoverageState.Deleted) { Description = "Purge the sample" })
                .AddCondition(new DelegateCondition<CoverageState, CoverageSample>("No booking refers to it", e => e.Bookings.Count == 0, nameof(CoverageSample.Bookings)));
            return workflow;
        }
    }
}
