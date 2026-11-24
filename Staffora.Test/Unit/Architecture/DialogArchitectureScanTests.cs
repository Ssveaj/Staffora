using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Services.Comments;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Unit.Architecture.DialogScanSamples;
using Staffora.Test.Unit.Workflow.Samples;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Unit.Architecture;

public class DialogArchitectureScanTests
{
    private static readonly Dictionary<Type, string> dialogToCome = new()
    {
    };

    private static readonly Dictionary<Type, string> shownInAnotherDialog = new()
    {
        [typeof(Staffora.Core.Entities.Resources.Resource)] = "ADR 0008: a staff member is shown in their user's dialog (its Staff view); there is no separate resource dialog",
        [typeof(Staffora.Core.Entities.Portal.Supplier)] = "ADR 0007: a customer portal's supplier party follows its staffing company and is no decision of the portal's people; every step of it is the system's, so nobody opens it, and the portal's home names it (TenantHomeService.ReadPortalAsync)",
    };

    private const string SamplesNamespace = "Staffora.Test.Unit.Architecture.DialogScanSamples";

    [Fact]
    public void Every_contexts_query_configurator_is_the_graph_its_load_method_uses()
    {
        var contexts = Contexts();

        Assert.Contains(typeof(TestParentDialogContext), contexts);
        Assert.Empty(contexts.SelectMany(GraphMismatches));
    }

    [Fact]
    public void The_graph_scan_passes_a_context_whose_load_uses_its_graph_and_catches_one_whose_does_not()
    {
        Assert.Empty(GraphMismatches(typeof(MatchingSampleContext)));
        Assert.Empty(GraphMismatches(typeof(TestParentDialogContext)));

        var mismatch = Assert.Single(GraphMismatches(typeof(MismatchedSampleContext)));
        Assert.Contains("names no SampleParentWorkflowQueries.WithWorkflowGraph", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_graph_scan_catches_a_load_that_applies_the_graph_to_a_side_query_only()
    {
        var mismatch = Assert.Single(GraphMismatches(typeof(SideQuerySampleContext)));

        Assert.Contains("SideQuerySampleService.LoadSampleParentUpdaterAsync reads the SampleParent without its workflow graph", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_graph_scan_reads_a_graph_and_a_load_a_context_inherits_from_its_base()
    {
        Assert.Empty(GraphMismatches(typeof(InheritedSampleContext)));
    }

    [Fact]
    public void The_graph_scan_follows_the_query_through_its_locals_so_a_graph_on_a_query_that_is_never_read_does_not_count()
    {
        var mismatch = Assert.Single(GraphMismatches(typeof(UnusedGraphSampleContext)));

        Assert.Contains("UnusedGraphSampleService.LoadSampleParentUpdaterAsync reads the SampleParent without its workflow graph", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_graph_scan_follows_a_read_into_a_helper_of_another_class()
    {
        Assert.Empty(GraphMismatches(typeof(HelperSampleContext)));

        var mismatch = Assert.Single(GraphMismatches(typeof(UngraphedHelperSampleContext)));
        Assert.Contains("reads the SampleParent without its workflow graph", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_contexts_step_with_its_edits_is_one_service_call_with_its_graph()
    {
        var contexts = Contexts();

        Assert.Contains(typeof(TestParentDialogContext), contexts);
        Assert.Contains(typeof(TestChildDialogContext), contexts);
        Assert.Empty(contexts.SelectMany(StepMismatches));
    }

    [Fact]
    public void The_step_scan_passes_a_step_with_the_graph_and_catches_one_without()
    {
        Assert.Empty(StepMismatches(typeof(MatchingSampleContext)));
        Assert.Empty(StepMismatches(typeof(InheritedSampleContext)));

        var mismatch = Assert.Single(StepMismatches(typeof(UngraphedStepSampleContext)));
        Assert.Contains("UngraphedStepSampleService.UpdateSampleParentAndTransitionAsync runs the step through no SampleParentWorkflowQueries.WithWorkflowGraph", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_context_that_creates_its_entity_builds_a_row_of_its_own()
    {
        var contexts = Contexts();

        Assert.Empty(contexts.SelectMany(CreateMismatches));
        Assert.Empty(CreateMismatches(typeof(MatchingSampleContext)));

        var mismatch = Assert.Single(CreateMismatches(typeof(PassThroughCreateSampleContext)));
        Assert.Contains("PassThroughCreateSampleService.CreateSampleParentAsync builds no SampleParent of its own", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_contexts_history_is_one_call_to_its_entitys_service()
    {
        var contexts = Contexts();

        Assert.Contains(typeof(TestParentDialogContext), contexts);
        Assert.Contains(typeof(TestChildDialogContext), contexts);
        Assert.Empty(contexts.SelectMany(HistoryMismatches));
    }

    [Fact]
    public void The_history_scan_passes_a_service_call_and_catches_a_context_that_goes_past_its_service()
    {
        Assert.Empty(HistoryMismatches(typeof(MatchingSampleContext)));
        Assert.Empty(HistoryMismatches(typeof(InheritedSampleContext)));

        var history = Assert.Single(HistoryMismatches(typeof(HistoryBypassSampleContext)));
        Assert.Contains("LoadHistoryAsync calls StateHistoryService.GetHistoryAsync itself", history, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_contexts_comment_threads_are_one_call_each_to_its_entitys_service()
    {
        var contexts = Contexts();

        Assert.Contains(typeof(TestParentDialogContext), contexts);
        Assert.True(DeclaresCommentThreads(typeof(TestParentDialogContext)));
        Assert.Empty(contexts.SelectMany(CommentMismatches));
    }

    [Fact]
    public void The_comment_scan_passes_a_service_call_and_catches_a_context_that_goes_past_its_service_or_reads_without_posting()
    {
        Assert.Empty(CommentMismatches(typeof(MatchingSampleContext)));
        Assert.Empty(CommentMismatches(typeof(InheritedSampleContext)));
        Assert.Empty(CommentMismatches(typeof(HistoryBypassSampleContext)));

        var read = Assert.Single(CommentMismatches(typeof(CommentReadBypassSampleContext)));
        Assert.Contains("LoadCommentThreadsAsync calls CommentService.GetThreadsAsync itself", read, StringComparison.Ordinal);

        var post = Assert.Single(CommentMismatches(typeof(CommentPostBypassSampleContext)));
        Assert.Contains("PostCommentAsync calls CommentService.AddCommentAsync itself", post, StringComparison.Ordinal);

        var twice = Assert.Single(CommentMismatches(typeof(CommentTwiceSampleContext)));
        Assert.Contains("hands the read to CommentService.GetThreadsAsync<SampleParent> 2 times", twice, StringComparison.Ordinal);

        var readOnly = Assert.Single(CommentMismatches(typeof(CommentReadOnlySampleContext)));
        Assert.Contains("overrides LoadCommentThreadsAsync without PostCommentAsync", readOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_contexts_custom_fields_and_editor_lookups_are_one_call_each_to_its_entitys_service()
    {
        var contexts = Contexts();
        var mismatches = new List<string>();
        foreach (var context in contexts)
        {
            var entity = EntityOf(context);
            if (Staffora.Core.Entities.EntityAttributes.CustomFieldCollection.Of(entity).Count > 0)
            {
                var call = ServiceCall(context, "LoadCustomFieldsAsync", $"Get{entity.Name}CustomFieldsAsync", out var mismatch);
                var reads = call is null ? 0 : IlScan.Reach(call, followOwnType: true).Count(called => called.DeclaringType == typeof(Staffora.Services.CustomFields.CustomFieldService) && called.Name == nameof(Staffora.Services.CustomFields.CustomFieldService.GetDefinitionsAsync));
                if (call is null || reads != 1)
                {
                    mismatches.Add(mismatch ?? $"{context.Name}: Get{entity.Name}CustomFieldsAsync reads the fields through CustomFieldService.GetDefinitionsAsync {reads} times; it does so exactly once");
                }
            }

            if (ContextChain(context).Any(type => type.GetMethod("LoadEditorOptionsAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null)
                && ServiceCall(context, "LoadEditorOptionsAsync", $"Get{entity.Name}EditorOptionsAsync", out var optionsMismatch) is null)
            {
                mismatches.Add(optionsMismatch!);
            }
        }

        Assert.Contains(typeof(TestParentDialogContext), contexts);
        Assert.Contains(typeof(TestChildDialogContext), contexts);
        Assert.Empty(mismatches);
    }

    [Fact]
    public void The_graph_scan_reads_the_test_parents_graph_in_its_context_and_in_its_services_load()
    {
        var graph = typeof(TestParentWorkflowQueries).GetMethod(nameof(TestParentWorkflowQueries.WithWorkflowGraph))!;

        Assert.Equal([graph], ConstructorGraphs(typeof(TestParentDialogContext)));
        Assert.Equal([graph], EntityReads(typeof(TestParentDialogService).GetMethod(nameof(TestParentDialogService.LoadTestParentUpdaterAsync))!, typeof(TestParent)));
    }

    [Fact]
    public void No_dialog_body_calls_a_service_in_its_lifecycle_methods()
    {
        var bodies = Bodies();

        Assert.Contains(typeof(TestParentDialog), bodies);
        Assert.Empty(bodies.SelectMany(ServiceCallsInLifecycle));
    }

    [Fact]
    public void The_body_scan_catches_a_service_call_in_OnInitializedAsync_and_passes_a_quiet_body_and_a_Radzen_UI_service()
    {
        Assert.Empty(ServiceCallsInLifecycle(typeof(QuietSampleBody)));
        Assert.Empty(ServiceCallsInLifecycle(typeof(RadzenSampleBody)));

        var call = Assert.Single(ServiceCallsInLifecycle(typeof(LoadingSampleBody)));
        Assert.Contains("MatchingSampleService.LoadSampleParentUpdaterAsync", call, StringComparison.Ordinal);
    }

    [Fact]
    public void The_body_scan_passes_a_body_that_opens_a_childs_dialog_and_still_catches_a_load_behind_the_door()
    {
        Assert.Empty(ServiceCallsInLifecycle(typeof(ChildDialogSampleBody)));

        var call = Assert.Single(ServiceCallsInLifecycle(typeof(ChildDialogLoadingSampleBody)));
        Assert.Contains("MatchingSampleService.LoadSampleParentUpdaterAsync", call, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_restore_host_makes_an_arrival()
    {
        var makers = new[] { typeof(StateEntityDialogContext).Assembly, typeof(DialogArchitectureScanTests).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
            .Where(method => IlScan.DirectReferences(method).Any(called => called.DeclaringType == typeof(StateEntityDialogArrival) && called.IsConstructor))
            .Select(method => OuterType(method.DeclaringType!))
            .Distinct()
            .ToList();

        Assert.Equal([typeof(StateEntityDialogRestoreHost), typeof(Staffora.Test.Integration.Web.DialogRestoreTests)], makers);
    }

    private static Type OuterType(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    [Fact]
    public void The_body_scan_exempts_only_a_real_graph_named_as_a_method_group_and_reads_any_other_method_named_like_one()
    {
        Assert.Contains("MatchingSampleService.LoadSampleParentUpdaterAsync", Assert.Single(ServiceCallsInLifecycle(typeof(SneakyGraphSampleBody))), StringComparison.Ordinal);

        Assert.Contains("EntityFrameworkQueryableExtensions.Include", Assert.Single(ServiceCallsInLifecycle(typeof(GraphCalledSampleBody))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_body_scan_reads_inside_a_graph_it_exempts_by_name_and_finds_a_load_there()
    {
        var found = Assert.Single(ServiceCallsInLifecycle(typeof(LoadingGraphGroupSampleBody)));
        Assert.Contains("TenantService.FetchTenantContentAsync", found, StringComparison.Ordinal);
        Assert.Contains("through the graph LoadingWorkflowQueries.WithWorkflowGraph", found, StringComparison.Ordinal);

        Assert.Empty(ServiceCallsInLifecycle(typeof(ChildDialogSampleBody)));
    }

    [Fact]
    public void Every_declared_list_views_load_is_one_call_to_its_entitys_service()
    {
        var contexts = Contexts();
        var checkedViews = contexts.Sum(context => ListViewLoads(context).Count);

        Assert.True(checkedViews >= 1, "No context declares a list view, so the scan proves nothing.");
        Assert.Empty(contexts.SelectMany(ViewLoadMismatches));
    }

    [Fact]
    public void The_view_load_scan_passes_one_service_call_and_catches_a_load_that_goes_past_the_service_or_calls_it_twice()
    {
        Assert.Empty(ViewLoadMismatches(typeof(ViewSampleContext)));

        var mismatch = Assert.Single(ViewLoadMismatches(typeof(ViewBypassSampleContext)));
        Assert.Contains("makes 2 calls to MatchingSampleService", mismatch, StringComparison.Ordinal);
        Assert.Contains("WorkflowService.GetAvailableTransitionsAsync", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_view_load_scan_follows_a_helper_and_counts_the_same_service_call_twice()
    {
        var helped = Assert.Single(ViewLoadMismatches(typeof(ViewHelperSampleContext)));
        Assert.Contains("goes past it to WorkflowService.GetAvailableTransitionsAsync", helped, StringComparison.Ordinal);
        Assert.Contains("makes 1 calls to MatchingSampleService", helped, StringComparison.Ordinal);

        Assert.Contains("makes 2 calls to MatchingSampleService", Assert.Single(ViewLoadMismatches(typeof(ViewTwiceSampleContext))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_view_load_scan_passes_one_call_through_a_child_service_that_checks_and_catches_two_or_one_that_checks_nothing()
    {
        Assert.Empty(ViewLoadMismatches(typeof(ChildServiceViewSampleContext)));

        Assert.Contains("makes 2 calls to ChildServiceSampleService", Assert.Single(ViewLoadMismatches(typeof(ChildServiceTwiceSampleContext))), StringComparison.Ordinal);

        Assert.Contains("goes past it to WorkflowService.GetAvailableTransitionsAsync", Assert.Single(ViewLoadMismatches(typeof(UncheckedChildServiceSampleContext))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_offered_with_a_permission_loads_only_through_a_child_method_that_checks_that_permission()
    {
        Assert.Empty(ViewLoadMismatches(typeof(ChildServiceOwnPermissionSampleContext)));

        Assert.Contains("whose check does not name the view's permission RolesView", Assert.Single(ViewLoadMismatches(typeof(ChildServiceOtherPermissionSampleContext))), StringComparison.Ordinal);

        Assert.True(ChildChecksPermission(typeof(Staffora.Services.Scheduling.ResourceAbsenceService), nameof(Staffora.Services.Scheduling.ResourceAbsenceService.QueryAbsencesOfUserAsync), RolePermission.ViewResourceAbsence));
        Assert.True(ChildChecksPermission(typeof(Staffora.Services.Scheduling.ResourceAbsenceService), nameof(Staffora.Services.Scheduling.ResourceAbsenceService.QueryAbsencesOfUserAsync), RolePermission.ManageResourceAbsence));
        Assert.False(ChildChecksPermission(typeof(Staffora.Services.Scheduling.ResourceAbsenceService), nameof(Staffora.Services.Scheduling.ResourceAbsenceService.QueryAbsencesOfUserAsync), RolePermission.RolesView));
    }

    [Theory]
    [InlineData(typeof(BaseClassSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(PublicHelperSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(StaticHelperSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(ExtensionSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(InterfaceSampleBody), "ISampleParentSource.ReadAsync")]
    [InlineData(typeof(ParametersSetSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(AfterRenderSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(RenderSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(ShouldRenderSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(DelegateFieldSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(VirtualDispatchSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(InterfaceImplementationSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    [InlineData(typeof(DeepSampleBody), "MatchingSampleService.LoadSampleParentUpdaterAsync")]
    public void The_body_scan_catches_a_service_call_however_it_hides(Type body, string call)
    {
        var found = Assert.Single(ServiceCallsInLifecycle(body));

        Assert.Contains(call, found, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_state_entity_has_exactly_one_dialog_context()
    {
        var contexts = Contexts();
        var byEntity = contexts.GroupBy(EntityOf).ToDictionary(group => group.Key, group => group.ToList());

        Assert.Empty(byEntity.Where(entry => entry.Value.Count > 1).Select(entry => $"{entry.Key.Name}: {string.Join(", ", entry.Value.Select(context => context.Name))}"));

        Assert.Empty(byEntity.Keys.Where(entity => !WorkflowRegistry.HasWorkflow(entity)).Select(entity => entity.Name));

        var appEntities = AppStateEntities();
        Assert.Contains(typeof(Tenant), appEntities);
        Assert.Empty(appEntities.Where(entity => !dialogToCome.ContainsKey(entity) && !shownInAnotherDialog.ContainsKey(entity) && !byEntity.ContainsKey(entity)).Select(entity => $"{entity.Name} has no dialog context"));
        Assert.Empty(dialogToCome.Concat(shownInAnotherDialog).Where(entry => byEntity.ContainsKey(entry.Key) || !appEntities.Contains(entry.Key) || string.IsNullOrWhiteSpace(entry.Value)).Select(entry => $"{entry.Key.Name}: the entry is stale"));

        Assert.Equal([typeof(OrganizationUnitDialogContext)], byEntity[typeof(Staffora.Core.Entities.Organization.OrganizationUnit)]);
        Assert.Equal([typeof(TenantRoleDialogContext)], byEntity[typeof(TenantRole)]);

        Assert.Equal([typeof(TestParentDialogContext)], byEntity[typeof(TestParent)]);
        Assert.Equal([typeof(TestChildDialogContext)], byEntity[typeof(TestChild)]);
    }

    [Fact]
    public void The_context_count_catches_two_contexts_over_one_entity()
    {
        var samples = SampleTypes().Where(IsContext).GroupBy(EntityOf).ToDictionary(group => group.Key, group => group.Count());

        Assert.Equal(38, samples[typeof(SampleParent)]);
    }

    private static IEnumerable<string> GraphMismatches(Type context)
    {
        var entity = EntityOf(context);
        var expected = $"{entity.Name}WorkflowQueries";
        var named = ConstructorGraphs(context);
        if (named.Count != 1 || named[0].DeclaringType?.Name != expected)
        {
            yield return $"{context.Name}: its constructor names {(named.Count == 0 ? $"no {expected}.WithWorkflowGraph" : string.Join(", ", named.Select(Describe)))} as its QueryConfigurator";
            yield break;
        }

        var load = ContextChain(context)
            .Select(type => type.GetMethod("LoadUpdaterAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .FirstOrDefault(method => method is not null);
        var serviceLoads = load is null ? [] : IlScan.Reach(load).Where(called => called.Name == $"Load{entity.Name}UpdaterAsync").Distinct().ToList();
        if (serviceLoads.Count != 1)
        {
            yield return $"{context.Name}: LoadUpdaterAsync calls {serviceLoads.Count} Load{entity.Name}UpdaterAsync methods; it makes exactly one service call";
            yield break;
        }

        var reads = EntityReads(serviceLoads[0], entity);
        if (reads.Count == 0)
        {
            yield return $"{context.Name}: {Describe(serviceLoads[0])} reads no {entity.Name} it could hand to an updater";
        }
        else if (reads.Any(graph => graph is null))
        {
            yield return $"{context.Name}: {Describe(serviceLoads[0])} reads the {entity.Name} without its workflow graph";
        }
        else if (reads.Any(graph => graph != named[0]))
        {
            yield return $"{context.Name}: its QueryConfigurator is {Describe(named[0])}, but {Describe(serviceLoads[0])} reads the {entity.Name} through {string.Join(", ", reads.Distinct().Select(graph => Describe(graph!)))}";
        }
    }

    private static List<MethodBase> ConstructorGraphs(Type context) =>
        [.. ContextChain(context)
            .SelectMany(type => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .SelectMany(constructor => IlScan.Reach(constructor))
            .Where(IsGraph)
            .Distinct()];

    private static IEnumerable<Type> ContextChain(Type context)
    {
        for (var type = context; type is not null && type != ContextBaseOf(context); type = type.BaseType)
        {
            yield return type;
        }
    }

    private static List<MethodBase?> EntityReads(MethodBase load, Type entity)
    {
        var reads = new List<MethodBase?>();
        var kept = new Dictionary<object, MethodBase?>();
        MethodBase? graph = null;
        foreach (var step in IlScan.Flow(load, method => IsReadIntoTheLoad(method, load, entity)))
        {
            switch (step)
            {
                case FlowStore store when IsQuery(store.Type):
                    kept[store.Slot] = graph;
                    break;
                case FlowLoad loaded when IsQuery(loaded.Type):
                    graph = kept.GetValueOrDefault(loaded.Slot);
                    break;
                case FlowCall { Method: var called } when IsGraph(called):
                    graph = called;
                    break;
                case FlowCall { Method: var called } when QueryEnd(called) is { } readsRows:
                    if (readsRows && ElementOf(called) == entity)
                    {
                        reads.Add(graph);
                    }

                    graph = null;
                    break;
                case FlowCall { Method: var called } when IsQuerySource(called):
                    graph = null;
                    break;
            }
        }

        return reads;
    }

    private static bool IsReadIntoTheLoad(MethodBase method, MethodBase load, Type entity) =>
        !IsGraph(method)
        && method is MethodInfo helper
        && (method.DeclaringType == load.DeclaringType
            || (IsStafforaCode(method) && method.DeclaringType!.Assembly.GetName().Name != "Staffora.Data" && Mentions(helper, entity)));

    private static bool Mentions(MethodInfo method, Type entity) =>
        Names(method.ReturnType, entity) || method.GetParameters().Any(parameter => Names(parameter.ParameterType, entity));

    private static bool Names(Type type, Type entity) =>
        type == entity || (type.HasElementType && Names(type.GetElementType()!, entity)) || (type.IsGenericType && type.GetGenericArguments().Any(argument => Names(argument, entity)));

    private static bool IsQuery(Type type) => typeof(IQueryable).IsAssignableFrom(type);

    private static bool IsQuerySource(MethodBase method) =>
        method is MethodInfo { ReturnType: var returned } info
        && IsQuery(returned)
        && !(info.IsStatic ? info.GetParameters() is [var first, ..] && IsQuery(first.ParameterType) : IsQuery(info.DeclaringType!));

    private static bool? QueryEnd(MethodBase method)
    {
        var name = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^"Async".Length] : method.Name;
        var isQueryOperator = method.DeclaringType is { } type
            && (type == typeof(Queryable) || type == typeof(Enumerable) || type.Name is "EntityFrameworkQueryableExtensions" || (type.IsGenericType && type.Name.StartsWith("DbSet", StringComparison.Ordinal)));
        if (!isQueryOperator)
        {
            return null;
        }

        return name switch
        {
            "First" or "FirstOrDefault" or "Single" or "SingleOrDefault" or "Last" or "LastOrDefault" or "ToList" or "ToArray" or "Find" => true,
            "Any" or "All" or "Count" or "LongCount" or "Sum" or "Min" or "Max" or "Average" or "Contains" => false,
            _ => null,
        };
    }

    private static Type? ElementOf(MethodBase method) =>
        method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments()[0]
        : method.DeclaringType is { IsGenericType: true } set ? set.GetGenericArguments()[0]
        : null;

    private static bool IsGraph(MethodBase method) =>
        method.Name == "WithWorkflowGraph" && method.DeclaringType?.Name.EndsWith("WorkflowQueries", StringComparison.Ordinal) == true;

    private static string Describe(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

    private static IEnumerable<string> StepMismatches(Type context)
    {
        var entity = EntityOf(context);
        if (ConstructorGraphs(context) is not [var graph])
        {
            yield break;
        }

        var step = ContextChain(context)
            .Select(type => type.GetMethod("SaveAndTransitionAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .FirstOrDefault(method => method is not null);
        var serviceSteps = step is null ? [] : IlScan.Reach(step).Where(called => called.Name == $"Update{entity.Name}AndTransitionAsync").Distinct().ToList();
        if (serviceSteps.Count != 1)
        {
            yield return $"{context.Name}: SaveAndTransitionAsync calls {serviceSteps.Count} Update{entity.Name}AndTransitionAsync methods; it makes exactly one service call";
            yield break;
        }

        var reach = IlScan.Reach(serviceSteps[0], followOwnType: true);
        var runs = reach.Count(called => called.DeclaringType == typeof(WorkflowService) && called.Name == nameof(WorkflowService.SaveAndTransitionAsync));
        if (runs != 1)
        {
            yield return $"{context.Name}: {Describe(serviceSteps[0])} hands the edits and the step to WorkflowService.SaveAndTransitionAsync {runs} times; it does so exactly once";
            yield break;
        }

        var graphs = reach.Where(IsGraph).Distinct().ToList();
        if (graphs.Count != 1 || graphs[0] != graph)
        {
            yield return $"{context.Name}: {Describe(serviceSteps[0])} runs the step through {(graphs.Count == 0 ? $"no {entity.Name}WorkflowQueries.WithWorkflowGraph" : string.Join(", ", graphs.Select(Describe)))}, but its QueryConfigurator is {Describe(graph)}";
        }
    }

    private static IEnumerable<string> CreateMismatches(Type context)
    {
        var entity = EntityOf(context);
        var create = ContextChain(context)
            .Select(type => type.GetMethod("CreateEntityAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .FirstOrDefault(method => method is not null);
        if (create is null || ConstructorGraphs(context) is not [var graph])
        {
            yield break;
        }

        var serviceCreates = IlScan.Reach(create).Where(called => called.Name == $"Create{entity.Name}Async").Distinct().ToList();
        if (serviceCreates.Count != 1)
        {
            yield return $"{context.Name}: CreateEntityAsync calls {serviceCreates.Count} Create{entity.Name}Async methods; it makes exactly one service call";
            yield break;
        }

        var reach = IlScan.Reach(serviceCreates[0], followOwnType: true);
        if (!reach.OfType<ConstructorInfo>().Any(constructor => constructor.DeclaringType == entity))
        {
            yield return $"{context.Name}: {Describe(serviceCreates[0])} builds no {entity.Name} of its own; it builds the row from the dialog's values rather than pass the dialog's entity on";
        }

        var runs = reach.Count(called => called.DeclaringType == typeof(WorkflowService) && called.Name is nameof(WorkflowService.CreateAndTransitionAsync) or nameof(WorkflowService.CreateManyAndTransitionAsync));
        var graphs = reach.Where(IsGraph).Distinct().ToList();
        if (runs != 1 || graphs.Count != 1 || graphs[0] != graph)
        {
            yield return $"{context.Name}: {Describe(serviceCreates[0])} hands the row to WorkflowService.CreateAndTransitionAsync {runs} times through {(graphs.Count == 0 ? "no graph" : string.Join(", ", graphs.Select(Describe)))}; it does so exactly once, through {Describe(graph)}";
        }
    }

    private static IEnumerable<string> HistoryMismatches(Type context)
    {
        var entity = EntityOf(context);
        var serviceCall = ServiceCall(context, "LoadHistoryAsync", $"Get{entity.Name}HistoryAsync", out var mismatch);
        if (serviceCall is null)
        {
            yield return mismatch!;
            yield break;
        }

        var handOn = IlScan.Reach(serviceCall, followOwnType: true)
            .Count(called => called.DeclaringType == typeof(StateHistoryService) && called.Name == nameof(StateHistoryService.GetHistoryAsync) && called.IsGenericMethod && called.GetGenericArguments()[0] == entity);
        if (handOn != 1)
        {
            yield return $"{context.Name}: {Describe(serviceCall)} hands the read to StateHistoryService.GetHistoryAsync<{entity.Name}> {handOn} times; it does so exactly once";
        }
    }

    private static readonly string[] commentMethods = ["LoadCommentThreadsAsync", "PostCommentAsync"];

    private static bool DeclaresCommentThreads(Type context) =>
        commentMethods.Any(name => ContextChain(context).Any(type => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null));

    private static IEnumerable<string> CommentMismatches(Type context)
    {
        var declared = commentMethods
            .Where(name => ContextChain(context).Any(type => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null))
            .ToList();
        if (declared.Count == 0)
        {
            yield break;
        }

        if (declared.Count == 1)
        {
            yield return $"{context.Name}: overrides {declared[0]} without {commentMethods.Single(name => name != declared[0])}; a context with comment threads reads and posts through its entity's service";
            yield break;
        }

        var entity = EntityOf(context);
        foreach (var (contextMethod, serviceMethod, handOnName) in new[]
        {
            ("LoadCommentThreadsAsync", $"Get{entity.Name}CommentsAsync", nameof(CommentService.GetThreadsAsync)),
            ("PostCommentAsync", $"Add{entity.Name}CommentAsync", nameof(CommentService.AddCommentAsync)),
        })
        {
            var serviceCall = ServiceCall(context, contextMethod, serviceMethod, out var mismatch);
            if (serviceCall is null)
            {
                yield return mismatch!;
                continue;
            }

            var handOn = IlScan.Reach(serviceCall, followOwnType: true)
                .Count(called => called.DeclaringType == typeof(CommentService) && called.Name == handOnName && called.IsGenericMethod && called.GetGenericArguments()[0] == entity);
            if (handOn != 1)
            {
                yield return $"{context.Name}: {Describe(serviceCall)} hands the {(contextMethod == "PostCommentAsync" ? "post" : "read")} to CommentService.{handOnName}<{entity.Name}> {handOn} times; it does so exactly once";
            }
        }
    }

    private static MethodBase? ServiceCall(Type context, string contextMethod, string serviceMethod, out string? mismatch)
    {
        var method = ContextChain(context)
            .Select(type => type.GetMethod(contextMethod, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .FirstOrDefault(found => found is not null);
        var reach = method is null ? [] : IlScan.Reach(method);
        var past = reach.Where(called => called.DeclaringType == typeof(StateHistoryService) || called.DeclaringType == typeof(WorkflowService) || called.DeclaringType == typeof(CommentService)).Select(Describe).Distinct().ToList();
        if (past.Count > 0)
        {
            mismatch = $"{context.Name}: {contextMethod} calls {string.Join(", ", past)} itself; it goes through {serviceMethod} of the entity's service, which checks the permission the entity's load checks";
            return null;
        }

        var calls = reach.Where(called => called.Name == serviceMethod).Distinct().ToList();
        if (calls.Count != 1)
        {
            mismatch = $"{context.Name}: {contextMethod} calls {calls.Count} {serviceMethod} methods; it makes exactly one service call";
            return null;
        }

        mismatch = null;
        return calls[0];
    }

    private static List<(string Key, MethodInfo Load, RolePermission? Permission)> ListViewLoads(Type context)
    {
        var declare = context.GetMethod("DeclareViews", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (declare.DeclaringType == typeof(StateEntityDialogContext))
        {
            return [];
        }

        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(context);
        var views = (IEnumerable<StateEntityDialogView>)declare.Invoke(instance, null)!;
        return [.. views
            .Where(view => view.GetType().IsGenericType && view.GetType().GetGenericTypeDefinition() == typeof(StateEntityDialogListView<>))
            .Select(view => (view.Key, ((Delegate)view.GetType().GetProperty("Load")!.GetValue(view)!).Method, view.Permission))];
    }

    private static IEnumerable<string> ViewLoadMismatches(Type context)
    {
        var entity = EntityOf(context);
        var load = ContextChain(context)
            .Select(type => type.GetMethod("LoadUpdaterAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .FirstOrDefault(method => method is not null);
        var service = load is null ? null : IlScan.Reach(load).FirstOrDefault(called => called.Name == $"Load{entity.Name}UpdaterAsync")?.DeclaringType;
        foreach (var (key, viewLoad, permission) in ListViewLoads(context))
        {
            if (service is null)
            {
                yield return $"{context.Name}: its view '{key}' loads, but the context's load names no entity service to load it through";
                continue;
            }

            var reach = IlScan.ReachAllReferences([viewLoad], (_, _, called) => IsStafforaCode(called) && called.DeclaringType != service && !IsBehindTheService(called) ? [called] : [])
                .Select(reference => reference.Referenced)
                .ToList();
            var doors = ChildServiceDoors(service).Where(door => reach.Contains(door.Getter)).ToList();
            bool ThroughADoor(MethodBase called) =>
                doors.Any(door => called.DeclaringType == door.Child && ServicePermissionScanTests.HoldsItsOwnCheck(door.Child, called.Name));
            var calls = reach.Count(called => (called.DeclaringType == service && !doors.Any(door => door.Getter == called)) || ThroughADoor(called));
            var past = reach.Where(called => called.DeclaringType != service && IsBehindTheService(called) && !ThroughADoor(called)).Select(Describe).Distinct().ToList();
            if (calls != 1 || past.Count > 0)
            {
                yield return $"{context.Name}: the load of its view '{key}' makes {calls} calls to {service.Name}{(past.Count > 0 ? $" and goes past it to {string.Join(", ", past)}" : string.Empty)}; it makes exactly one call to the entity's service, which checks the view's permission and the tenant";
            }

            if (permission is { } viewPermission)
            {
                foreach (var called in reach.Where(ThroughADoor).Distinct().Where(called => !ChildChecksPermission(called.DeclaringType!, called.Name, viewPermission)))
                {
                    yield return $"{context.Name}: the load of its view '{key}' goes through {Describe(called)}, whose check does not name the view's permission {viewPermission}; a child service the view loads through checks the view's own permission";
                }
            }
        }
    }

    private static bool ChildChecksPermission(Type child, string method, RolePermission permission)
    {
        var code = ServiceSources.Of(child);
        var helpers = ServiceSources.Members(code, "private");
        var bodies = new List<string>(ServiceSources.PublicBodiesOf(code, method));
        var followed = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < bodies.Count; index++)
        {
            foreach (var helper in helpers.Where(helper => Regex.IsMatch(bodies[index], $@"\b{helper.Name}\s*\(") && followed.Add(helper.Name)))
            {
                bodies.Add(helper.Body);
            }
        }

        var text = string.Join("\n", bodies);
        if (Regex.IsMatch(text, $@"\bRolePermission\.{permission}\b"))
        {
            return true;
        }

        return PermissionLists().Any(list =>
            Regex.IsMatch(text, list.Field.DeclaringType == child ? $@"(?<![\w.])(?:{child.Name}\.)?{list.Field.Name}\b" : $@"\b{list.Field.DeclaringType!.Name}\.{list.Field.Name}\b")
            && list.Permissions.Contains(permission));
    }

    private static IEnumerable<(FieldInfo Field, IReadOnlyCollection<RolePermission> Permissions)> PermissionLists() =>
        typeof(WorkflowService).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(field => typeof(IEnumerable<RolePermission>).IsAssignableFrom(field.FieldType) && !field.DeclaringType!.IsGenericTypeDefinition)
            .Select(field => (field, (IReadOnlyCollection<RolePermission>)[.. (IEnumerable<RolePermission>)(field.GetValue(null) ?? Array.Empty<RolePermission>())]));

    private static IEnumerable<(MethodInfo Getter, Type Child)> ChildServiceDoors(Type service) =>
        service.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetMethod is not null && ServicePermissionScanTests.HeldServices.Contains(property.PropertyType))
            .Select(property => (property.GetMethod!, property.PropertyType));

    private static bool IsBehindTheService(MethodBase method) =>
        method.DeclaringType?.Assembly.GetName().Name is { } assembly
        && (assembly is "Staffora.Services" or "Staffora.Data" || assembly.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));

    private static readonly HashSet<string> lifecycle = new(StringComparer.Ordinal)
    {
        "OnInitialized", "OnInitializedAsync", "OnParametersSet", "OnParametersSetAsync", "OnAfterRender", "OnAfterRenderAsync", "SetParametersAsync",
        "BuildRenderTree", "ShouldRender",
    };

    private static IEnumerable<string> ServiceCallsInLifecycle(Type body)
    {
        var chain = BodyChain(body).ToList();
        var injected = chain
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => !IsUiService(type))
            .ToList();

        var roots = chain
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method => lifecycle.Contains(method.Name))
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)));

        var references = IlScan.ReachAllReferences(roots, (container, code, called) => IsStafforaCode(called) && !IsServiceCall(called, injected) && !IsJudgedWhereCalled(container, code, called) ? [called, .. Dispatches(called)] : []);
        var calls = references
            .Where(reference => IsServiceCall(reference.Referenced, injected) && !IsJudgedWhereCalled(reference.In, reference.Code, reference.Referenced))
            .Select(reference => $"{body.Name} calls {Describe(reference.Referenced)} in its lifecycle");

        var graphs = references
            .Where(reference => IsWorkflowGraph(reference.Referenced) && IsJudgedWhereCalled(reference.In, reference.Code, reference.Referenced))
            .Select(reference => reference.Referenced)
            .Distinct();
        var throughGraphs = graphs.SelectMany(graph => LoadsInGraph(graph).Select(load => $"{body.Name} calls {Describe(load)} in its lifecycle, through the graph {Describe(graph)}"));
        return calls.Concat(throughGraphs).Distinct();
    }

    private static IEnumerable<MethodBase> LoadsInGraph(MethodBase graph) =>
        IlScan.ReachAllReferences([graph], (_, _, called) => IsStafforaCode(called) && (!IsServiceCall(called, []) || IsGraphShaped(called)) ? [called] : [])
            .Select(reference => reference.Referenced)
            .Where(called => IsServiceCall(called, []) && !IsGraphShaped(called) && !IsQueryShaping(called))
            .Distinct();

    private static bool IsGraphShaped(MethodBase method) => method.IsStatic && IsConfiguratorShape(method);

    private static bool IsQueryShaping(MethodBase method) =>
        method.DeclaringType == typeof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions)
        && queryShaping.Contains(method.Name);

    private static readonly HashSet<string> queryShaping = new(StringComparer.Ordinal)
    {
        "Include", "ThenInclude", "AsSplitQuery", "AsSingleQuery", "AsNoTracking", "AsNoTrackingWithIdentityResolution", "AsTracking", "TagWith", "TagWithCallSite",
    };

    private static bool IsJudgedWhereCalled(MethodBase container, System.Reflection.Emit.OpCode code, MethodBase method) =>
        (IsWorkflowGraph(method) && (code == System.Reflection.Emit.OpCodes.Ldftn || code == System.Reflection.Emit.OpCodes.Ldvirtftn || (IlScan.IsGenerated(container) && IsConfiguratorShape(container))))
        || (method.Name == "HasChanges" && method.DeclaringType is { IsGenericType: true } updater && updater.GetGenericTypeDefinition() == typeof(Staffora.Data.DetachedEntityUpdater<>))
        || method == loadState
        || method == admit;

    private static readonly MethodInfo loadState = typeof(StateEntityDialogContext).GetProperty(nameof(StateEntityDialogContext.LoadState))!.GetMethod!;

    private static readonly MethodInfo admit = typeof(StateEntityDialogArrival).GetMethod(nameof(StateEntityDialogArrival.AdmitAsync))!;

    private static bool IsWorkflowGraph(MethodBase method) =>
        method is MethodInfo { IsStatic: true, Name: "WithWorkflowGraph" } graph
        && IsConfiguratorShape(graph)
        && (graph.DeclaringType?.Assembly.GetName().Name is "Staffora.Data" or "Staffora.Services" || testGraphs.Contains(graph.DeclaringType!));

    private static readonly HashSet<Type> testGraphs = [typeof(TestParentWorkflowQueries), typeof(TestChildWorkflowQueries), typeof(SampleIncludeWorkflowQueries), typeof(LoadingWorkflowQueries)];

    private static bool IsConfiguratorShape(MethodBase method) =>
        method is MethodInfo info
        && info.GetParameters() is [var parameter]
        && parameter.ParameterType is { IsGenericType: true } input && input.GetGenericTypeDefinition() == typeof(IQueryable<>)
        && info.ReturnType == input;

    private static readonly Dictionary<MethodBase, IReadOnlyList<MethodBase>> dispatches = [];

    private static IReadOnlyList<MethodBase> Dispatches(MethodBase called)
    {
        if (called is not MethodInfo { IsVirtual: true } method || method.DeclaringType is not { } declaring
            || declaring.Assembly.GetName().Name == "Staffora.Core" || (method.IsFinal && !declaring.IsInterface))
        {
            return [];
        }

        lock (dispatches)
        {
            if (dispatches.TryGetValue(method, out var known))
            {
                return known;
            }
        }

        var targets = new List<MethodBase>();
        var definition = method.GetBaseDefinition();
        foreach (var type in StafforaTypes().Where(type => type.IsClass && !type.Name.Contains('<', StringComparison.Ordinal)))
        {
            if (declaring.IsInterface)
            {
                var contract = type.GetInterfaces().FirstOrDefault(candidate => SameType(candidate, declaring));
                if (contract is null || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                var map = type.GetInterfaceMap(contract);
                var index = Array.FindIndex(map.InterfaceMethods, candidate => candidate.MetadataToken == method.MetadataToken && candidate.Module == method.Module);
                if (index >= 0 && map.TargetMethods[index].DeclaringType == type)
                {
                    targets.Add(map.TargetMethods[index]);
                }
            }
            else if (DerivesFrom(type, declaring))
            {
                targets.AddRange(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(candidate => candidate.Name == method.Name && candidate.GetBaseDefinition() is var root && root.MetadataToken == definition.MetadataToken && root.Module == definition.Module));
            }
        }

        lock (dispatches)
        {
            dispatches[method] = targets;
        }

        return targets;
    }

    private static bool DerivesFrom(Type type, Type declaring)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SameType(current, declaring))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameType(Type candidate, Type wanted) =>
        candidate == wanted || (candidate.IsGenericType && wanted.IsGenericType && candidate.GetGenericTypeDefinition() == wanted.GetGenericTypeDefinition());

    private static IReadOnlyList<Type>? stafforaTypes;

    private static IReadOnlyList<Type> StafforaTypes() => stafforaTypes ??=
        [.. new[] { typeof(StateEntityDialogContext).Assembly, typeof(WorkflowService).Assembly, typeof(Staffora.Data.ApplicationDbContext).Assembly, typeof(DialogArchitectureScanTests).Assembly }
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())];

    private static IEnumerable<Type> BodyChain(Type body)
    {
        for (var type = body; type is not null && IsStafforaType(type); type = type.BaseType)
        {
            yield return type;
        }
    }

    private static bool IsServiceCall(MethodBase method, IReadOnlyList<Type> injected)
    {
        var type = method.DeclaringType;
        if (type is null)
        {
            return false;
        }

        var assembly = type.Assembly.GetName().Name ?? string.Empty;
        return assembly is "Staffora.Services" or "Staffora.Data"
            || assembly.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || injected.Any(service => service.IsAssignableFrom(type) || (IsStafforaType(type) && type.IsAssignableFrom(service)))
            || (typeof(StateEntityDialogContext).IsAssignableFrom(type) && method.Name.Contains("Load", StringComparison.Ordinal));
    }

    private static bool IsUiService(Type type) =>
        type == typeof(TimeProvider)
        || type.Namespace is { } name && (name == "Radzen" || name.StartsWith("Radzen.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.JSInterop", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal));

    private static bool IsStafforaCode(MethodBase method) => method.DeclaringType is { } type && IsStafforaType(type);

    private static bool IsStafforaType(Type type) => type.Assembly.GetName().Name?.StartsWith("Staffora.", StringComparison.Ordinal) == true;

    private static List<Type> Contexts() => [.. ScannedTypes().Where(IsContext)];

    private static List<Type> Bodies() =>
        [.. ScannedTypes().Where(type => !type.IsAbstract && type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IStateEntityDialog<>)))];

    private static bool IsContext(Type type) => !type.IsAbstract && ContextBaseOf(type) is not null;

    private static Type EntityOf(Type context) => ContextBaseOf(context)!.GetGenericArguments()[1];

    private static Type? ContextBaseOf(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(StateEntityDialogContextBase<,>))
            {
                return current;
            }
        }

        return null;
    }

    private static IEnumerable<Type> ScannedTypes() =>
        new[] { typeof(StateEntityDialogContext).Assembly, typeof(DialogArchitectureScanTests).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace != SamplesNamespace);

    private static IEnumerable<Type> SampleTypes() =>
        typeof(DialogArchitectureScanTests).Assembly.GetTypes().Where(type => type.Namespace == SamplesNamespace);

    private static HashSet<Type> AppStateEntities() =>
        [.. typeof(Tenant).Assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && WorkflowRegistry.HasWorkflow(type))];
}
