using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Components.Common.WorkflowFields;
using Staffora.Blazor.Components.EntityAttributes;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Data;
using Staffora.Test.Unit.Architecture.DialogScanSamples;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Unit.Architecture;

public partial class StateFieldScanTests
{
    private const string SamplesNamespace = "Staffora.Test.Unit.Architecture.DialogScanSamples";

    private static readonly HashSet<Type> foundation =
    [
        typeof(StateEntityDialogContext),
        typeof(StateEntityDialogContextBase<,>),
        typeof(CustomFieldRequirement),
        typeof(StateFieldBase<>),
        typeof(StateFieldTextBox),
        typeof(StateFieldTextArea),
        typeof(StateFieldNumeric<>),
        typeof(StateFieldDatePicker<>),
        typeof(StateFieldDropDown<,>),
        typeof(StateFieldSwitch),
        typeof(StateFieldFrame),
        typeof(StateAreaFieldset),
        typeof(StatePermissionMatrix),
        typeof(StateWorkPatternSegments),
        typeof(StateJobTimetable),
        typeof(StateSlotQualifications<>),
        typeof(StateOrderLines),
        typeof(StateChoiceOptions),
        typeof(StateFieldTime),
        typeof(StateFieldDayAndTime<>),
        typeof(StateCustomFields<>),
        typeof(StateEntityDialogViewPanel),
        typeof(StateEntityDialogChildList<>),
        typeof(StateEntityDialogSession),
        typeof(StateEntityDialogTarget),
        typeof(AttributeValueField),
        typeof(CustomFieldsFieldset<>),
        typeof(FieldLabel),
        typeof(DialogServiceExtensions),

        typeof(Staffora.Blazor.Components.Coordination.SlotStaffingPanel),
        typeof(Staffora.Blazor.Components.Coordination.JobStaffSection),
        typeof(Staffora.Blazor.Components.Coordination.JobStaffingView),
        typeof(Staffora.Blazor.Components.Coordination.AbsenceImpactView),
    ];

    private static readonly HashSet<string> bodyContextMembers = new(StringComparer.Ordinal)
    {
        ".ctor", "get_Entity", "get_Version", "get_Lifetime", "get_DisplayName", "set_DisplayName", "NotifyChanged", "NotifyEdited",
        "get_FooterDisabled", "set_FooterDisabled", "get_FooterActions", "set_FooterActions", "get_OnBeforeTransition", "set_OnBeforeTransition",
        "get_TransitionFilter", "set_TransitionFilter", "get_TransitionLabel", "set_TransitionLabel",
        "set_DefaultTitle", "set_CreateTitle", "set_EntityIcon", "set_UnsavedChangesMessage",
    };

    private static readonly HashSet<string> createModeMembers = new(StringComparer.Ordinal)
    {
        "get_IsCreate", "get_EntityId", "get_UrlSegment", "get_Updater", "get_HasSaved", "get_DialogResult", "get_Title", "get_CreateTitle", "get_DefaultTitle",
    };

    private static readonly HashSet<string> stampedMembers = new(StringComparer.Ordinal)
    {
        "get_Id", "get_TenantId", "get_Created", "get_CreatedById", "get_Modified", "get_ModifiedById", "get_ImpersonatedById",
    };

    private static readonly HashSet<string> accessAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "disabled", "readonly", "required", "hidden", "inert", "visible", "aria-disabled", "aria-readonly", "aria-required",
    };

    private static readonly HashSet<string> inputElements = new(StringComparer.OrdinalIgnoreCase) { "input", "select", "textarea" };

    private static readonly Dictionary<string, string> namedArguments = new(StringComparer.Ordinal)
    {
        [nameof(RenderTreeBuilder.AddAttribute)] = "name",
        [nameof(RenderTreeBuilder.AddComponentParameter)] = "name",
        [nameof(RenderTreeBuilder.OpenElement)] = "elementName",
        [nameof(RenderTreeBuilder.AddMarkupContent)] = "markupContent",
    };

    [Fact]
    public void No_dialog_body_or_context_contains_a_hand_written_visibility_read_only_or_required_check()
    {
        var bodies = Scanned().Where(IsBody).ToList();
        var contexts = Scanned().Where(IsContext).ToList();

        Assert.Contains(typeof(TestParentDialog), bodies);
        Assert.Contains(typeof(TestChildDialog), bodies);
        Assert.Contains(typeof(TestParentDialogContext), contexts);
        Assert.Contains(typeof(TestChildDialogContext), contexts);
        Assert.Empty(OwnThreads.Map([.. bodies, .. contexts], OwnThreads.Wide, type => HandWrittenChecks(type).ToList()).SelectMany(checks => checks));
    }

    [Fact]
    public void The_scan_passes_a_body_that_places_its_fields_through_the_state_fields_and_uses_the_edit_channel()
    {
        Assert.Empty(HandWrittenChecks(typeof(StateFieldSampleBody)));
        Assert.Empty(HandWrittenChecks(typeof(EditChannelSampleBody)));
        Assert.Empty(HandWrittenChecks(typeof(MatchingSampleContext)));
    }

    [Fact]
    public void A_foreach_over_a_framework_collection_disposes_its_own_enumerator_while_a_dispose_through_the_interface_dispatches_to_every_one()
    {
        Assert.Empty(HandWrittenChecks(typeof(ForeachSampleBody)));
        Assert.Equal(typeof(List<string>.Enumerator), IlScan.ConstrainedTarget(typeof(List<string>.Enumerator), typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!).DeclaringType);

        Assert.NotEmpty(HandWrittenChecks(typeof(InterfaceDisposeSampleBody)));

        Assert.Empty(HandWrittenChecks(typeof(InterfaceForeachSampleBody)));
        Assert.Equal(typeof(IDisposable), IlScan.ConstrainedTarget(typeof(IComparable), typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!).DeclaringType);
    }

    [Fact]
    public void The_foundation_is_the_shared_types_of_the_app_alone()
    {
        Assert.All(foundation, type => Assert.Equal("Staffora.Blazor", type.Assembly.GetName().Name));

        Assert.False(IsFoundation(typeof(Staffora.Blazor.Helper.ScanSamples.NamespaceHelper)));
        Assert.False(IsFoundation(typeof(Staffora.Blazor.Components.Common.Dialogs.StateEntity.ScanSamples.DialogNamespaceHelper)));
        Assert.True(IsFoundation(typeof(StateFieldDropDown<Guid, string>)));
    }

    [Theory]
    [InlineData(typeof(StateBranchSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(StoredStateSampleBody), "reads the state (StateEntityDialogContext.get_State)")]
    [InlineData(typeof(FieldConfigSampleBody), "reads the field access (Workflow`2.GetFieldConfig)")]
    [InlineData(typeof(FieldConfigSampleBody), "reads the field access (StateFieldConfig`1.IsEditable)")]
    [InlineData(typeof(HelperStateSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(EntityPropertySampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(ExpressionSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(FlagsSampleBody), "works with FieldAccessFlags")]
    [InlineData(typeof(ContextAccessSampleBody), "reads the field access (StateEntityDialogContext.FieldAccessOf)")]
    [InlineData(typeof(RequirementSampleBody), "reads the field access (StateEntityDialogContext.RequirementOf)")]
    [InlineData(typeof(CreateFlagSampleBody), "decides by create mode (StateEntityDialogContext.get_IsCreate)")]
    [InlineData(typeof(EmptyIdSampleBody), "compares an id with Guid.Empty")]
    [InlineData(typeof(DisabledParameterSampleBody), "writes the access into its markup (Disabled)")]
    [InlineData(typeof(RawInputSampleBody), "binds a raw input (RadzenTextBox)")]
    [InlineData(typeof(RawElementSampleBody), "writes an input element of its own (input)")]
    [InlineData(typeof(RawElementSampleBody), "writes the access into its markup (readonly)")]
    [InlineData(typeof(MarkupSampleBody), "writes the access into its static markup")]
    [InlineData(typeof(FieldAccessSampleContext), "reads the field access (Workflow`2.GetFieldConfig)")]
    [InlineData(typeof(EntityIdSampleBody), "decides by create mode (StateEntityDialogContext.get_EntityId)")]
    [InlineData(typeof(UpdaterNullSampleBody), "decides by create mode (StateEntityDialogContextBase`2.get_Updater)")]
    [InlineData(typeof(UpdaterSnapshotSampleBody), "decides by create mode (StateEntityDialogContextBase`2.get_Updater)")]
    [InlineData(typeof(UpdaterSnapshotSampleBody), "reads the updater (DetachedEntityUpdater`1.OriginalValue)")]
    [InlineData(typeof(UrlSegmentSampleBody), "decides by create mode (StateEntityDialogContext.get_UrlSegment)")]
    [InlineData(typeof(TitleSampleBody), "decides by create mode (StateEntityDialogContext.get_CreateTitle)")]
    [InlineData(typeof(TitleSampleBody), "decides by create mode (StateEntityDialogContext.get_Title)")]
    [InlineData(typeof(HasSavedSampleBody), "decides by create mode (StateEntityDialogContext.get_HasSaved)")]
    [InlineData(typeof(DefaultIdSampleBody), "decides by create mode (reads TenantEntityBase.get_Id)")]
    [InlineData(typeof(AuditColumnSampleBody), "decides by create mode (reads TenantEntityBase.get_Created)")]
    [InlineData(typeof(InTransitionSampleBody), "reads the state (TenantStateEntityBase`1.get_IsInTransition)")]
    [InlineData(typeof(InTransitionSampleBody), "reads what the dialog keeps for its own parts (StateEntityDialogContext.get_IsBusy)")]
    [InlineData(typeof(HelperNamespaceSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(DialogNamespaceSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(InstanceHelperSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(ConstructorHelperSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(InterfaceHelperSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(FrameworkInterfaceSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(StaticDelegateSampleBody), "reads the state (TenantStateEntityBase`1.get_State)")]
    [InlineData(typeof(GenericHelperSampleBody), "reads the state (IStateEntity`1.get_State)")]
    [InlineData(typeof(WrapperComponentSampleBody), "binds a raw input (RadzenTextBox)")]
    [InlineData(typeof(DynamicComponentSampleBody), "binds a raw input (DynamicComponent)")]
    [InlineData(typeof(DynamicComponentSampleBody), "names a raw input type (RadzenTextBox)")]
    [InlineData(typeof(RuntimeTypeSampleBody), "opens a component by a runtime type")]
    [InlineData(typeof(RuntimeTypeSampleBody), "names a raw input type (RadzenTextBox)")]
    [InlineData(typeof(BuiltAttributeSampleBody), "builds the name of AddAttribute at run time")]
    [InlineData(typeof(BuiltAttributeSampleBody), "splats attributes it builds itself (AddMultipleAttributes)")]
    [InlineData(typeof(BuiltAttributeSampleBody), "decides by create mode (StateEntityDialogContext.get_HasSaved)")]
    [InlineData(typeof(BuiltElementSampleBody), "builds the elementName of OpenElement at run time")]
    [InlineData(typeof(BuiltElementSampleBody), "builds the markupContent of AddMarkupContent at run time")]
    [InlineData(typeof(RuntimeMarkupSampleBody), "writes runtime markup (MarkupString)")]
    [InlineData(typeof(BuiltParameterSampleBody), "builds the name of AddComponentParameter at run time")]
    [InlineData(typeof(DynamicSampleBody), "uses dynamic")]
    public void The_scan_catches_a_hand_written_check_however_it_is_written(Type sample, string check)
    {
        var found = HandWrittenChecks(sample).ToList();

        Assert.True(found.Any(finding => finding.Contains(check, StringComparison.Ordinal)), $"Found: {string.Join(" | ", found)}");
    }

    private static IEnumerable<string> HandWrittenChecks(Type type)
    {
        var body = IsBody(type);
        var chain = Chain(type).ToList();
        var roots = chain.SelectMany(OwnCode);
        var (operands, bodies) = IlScan.Walk(roots, operand => body ? FollowFromBody(operand) : FollowFromContext(operand, chain));

        var found = operands.Select(found => Check(found.Operand, body) is { } check ? $"{type.Name} {check} in {Describe(found.In)}" : null);
        if (body)
        {
            found = found.Concat(bodies.SelectMany(read => BuiltNames(read).Select(check => $"{type.Name} {check} in {Describe(read)}")));
        }

        return found.OfType<string>().Distinct();
    }

    private static IEnumerable<MethodBase> OwnCode(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));

    private static IEnumerable<MethodBase> FollowFromContext(object operand, IReadOnlyList<Type> chain)
    {
        if (operand is IlScan.ReceiverCall call)
        {
            operand = call.Method;
        }

        if (operand is MethodBase method && method.DeclaringType is { } declaring && IsStaffora(declaring) && !IsFoundation(declaring)
            && (chain.Contains(declaring) || method.IsStatic || typeof(IStateEntity).IsAssignableFrom(declaring)))
        {
            yield return method;
        }
    }

    private static IEnumerable<MethodBase> FollowFromBody(object operand)
    {
        switch (operand)
        {
            case IlScan.ReceiverCall call:
                if (Reads(call.Method.DeclaringType))
                {
                    yield return call.Method;
                }

                foreach (var dispatched in Dispatched(call.Method).Where(dispatched => dispatched.DeclaringType is { } declaring && call.MayBe(declaring)))
                {
                    yield return dispatched;
                }

                break;
            case ConstructorInfo constructor when Reads(constructor.DeclaringType):
                yield return constructor;
                break;
            case MethodInfo { Name: nameof(RenderTreeBuilder.OpenComponent), IsGenericMethod: true } open when open.DeclaringType == typeof(RenderTreeBuilder)
                && open.GetGenericArguments()[0] is var component && Reads(component):
                foreach (var member in Chain(component).SelectMany(OwnCode))
                {
                    yield return member;
                }

                break;
            case MethodInfo method when Reads(method.DeclaringType):
                yield return method;
                foreach (var dispatched in Dispatched(method))
                {
                    yield return dispatched;
                }

                break;
            case MethodInfo method when method.IsVirtual || method.IsAbstract:
                foreach (var dispatched in Dispatched(method))
                {
                    yield return dispatched;
                }

                break;
            case FieldInfo field when Reads(field.DeclaringType):
                foreach (var constructor in field.DeclaringType!.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    yield return constructor;
                }

                if (field.DeclaringType!.TypeInitializer is { } initializer)
                {
                    yield return initializer;
                }

                break;
        }
    }

    private static bool Reads(Type? type) => type is not null && IsStaffora(type) && !IsFoundation(type);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type Declaring, string Name), IReadOnlyList<MethodBase>> dispatched = new();

    private static IReadOnlyList<MethodBase> Dispatched(MethodInfo method)
    {
        if (!method.IsVirtual && !method.IsAbstract || method.DeclaringType is not { } declaring)
        {
            return [];
        }

        var definition = declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : declaring;
        return dispatched.GetOrAdd((definition, method.Name), key => [.. StafforaTypes.Value
            .Where(candidate => !candidate.IsInterface && Reads(candidate))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(candidate => candidate.Name.EndsWith(key.Name, StringComparison.Ordinal) && Implements(candidate, key.Declaring))]);
    }

    private static bool Implements(MethodInfo candidate, Type declaring)
    {
        if (candidate.GetBaseDefinition().DeclaringType is { } baseType && (baseType.IsGenericType ? baseType.GetGenericTypeDefinition() : baseType) == declaring && candidate.GetBaseDefinition() != candidate)
        {
            return true;
        }

        if (!declaring.IsInterface || candidate.DeclaringType is not { } implementer)
        {
            return false;
        }

        foreach (var contract in implementer.GetInterfaces().Where(contract => (contract.IsGenericType ? contract.GetGenericTypeDefinition() : contract) == declaring))
        {
            try
            {
                if (implementer.GetInterfaceMap(contract).TargetMethods.Contains(candidate))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Lazy<IReadOnlyList<Type>> StafforaTypes = new(() =>
        [.. new[] { typeof(StateEntityDialogContext).Assembly, typeof(StateFieldScanTests).Assembly, typeof(IStateEntity).Assembly }.SelectMany(assembly => assembly.GetTypes())]);

    private static string? Check(object operand, bool body)
    {
        switch (operand)
        {
            case MethodBase method:
                return CheckMember(method) ?? (body ? CheckBodyMember(method) : null);
            case TokenOperand { Member: MethodBase method }:
                return CheckMember(method) ?? (body ? CheckBodyMember(method) : null);
            case TokenOperand { Member: Type type } when type == typeof(FieldAccessFlags):
            case Type flags when flags == typeof(FieldAccessFlags):
                return "works with FieldAccessFlags";
            case TokenOperand { Member: Type type } when body && IsRawInput(type):
                return $"names a raw input type ({Short(type)})";
            case Type type when body && IsRawInput(type):
                return $"names a raw input type ({Short(type)})";
            case FieldInfo field when field.DeclaringType == typeof(Guid) && field.Name == nameof(Guid.Empty):
                return "compares an id with Guid.Empty";
            case FieldInfo field when field.FieldType == typeof(FieldAccessFlags):
                return "works with FieldAccessFlags";
            case StringOperand { Value: var text } when body:
                return CheckMarkup(text);
            default:
                return null;
        }
    }

    private static string? CheckMember(MethodBase method)
    {
        var declaring = method.DeclaringType;
        if (declaring is null)
        {
            return null;
        }

        var name = $"{declaring.Name}.{method.Name}";
        var definition = declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : declaring;
        var onEntity = typeof(IStateEntity).IsAssignableFrom(declaring) || definition == typeof(IStateEntity<>);
        if ((method.Name == "get_State" && (onEntity || declaring == typeof(StateEntityDialogContext))) || (method.Name == "get_IsInTransition" && onEntity))
        {
            return $"reads the state ({name})";
        }

        var fieldAccess = definition == typeof(StateFieldConfig<>) || definition == typeof(StateFieldBuilder<>) || definition == typeof(AreaFieldBuilder<>)
            || (definition == typeof(Workflow<,>) && method.Name is nameof(Workflow<,>.GetFieldConfig) or "Staffora.Core.Workflow.IWorkflow.GetFieldAccess"
                or nameof(Workflow<,>.RequiresCustomFieldsIn) or nameof(Workflow<,>.StepsRequiringCustomFieldsFrom))
            || (declaring == typeof(IWorkflow) && method.Name is nameof(IWorkflow.GetFieldAccess) or nameof(IWorkflow.DeclaresField) or nameof(IWorkflow.RequiresCustomFieldsIn))
            || (definition == typeof(WorkflowState<,>) && method.Name == "get_Fields")
            || (definition == typeof(StateEntityDialogContextBase<,>) && method.Name is "get_Workflow" or "FieldAccessOf" or "AreaAccessOf" or "RequirementOf")
            || (declaring == typeof(StateEntityDialogContext) && method.Name is "get_EntityWorkflow" or "FieldAccessOf" or "AreaAccessOf" or "RequirementOf")
            || declaring == typeof(WorkflowRegistry)
            || (declaring == typeof(WorkflowExtensions) && method.Name == nameof(WorkflowExtensions.GetWorkflow))
            || (method is MethodInfo { ReturnType: var returned } && returned == typeof(FieldAccessFlags))
            || method.GetParameters().Any(parameter => parameter.ParameterType == typeof(FieldAccessFlags));
        if (fieldAccess)
        {
            return $"reads the field access ({name})";
        }

        return declaring == typeof(StateEntityDialogContext) && method.Name == "get_IsCreate" ? $"decides by create mode ({name})" : null;
    }

    private static string? CheckBodyMember(MethodBase method)
    {
        var declaring = method.DeclaringType;
        if (declaring is null)
        {
            return null;
        }

        var name = $"{declaring.Name}.{method.Name}";
        var definition = declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : declaring;
        if (declaring == typeof(StateEntityDialogContext) || definition == typeof(StateEntityDialogContextBase<,>))
        {
            return createModeMembers.Contains(method.Name) ? $"decides by create mode ({name})"
                : bodyContextMembers.Contains(method.Name) ? null
                : $"reads what the dialog keeps for its own parts ({name})";
        }

        if (definition == typeof(DetachedEntityUpdater<>))
        {
            return $"reads the updater ({name})";
        }

        if (stampedMembers.Contains(method.Name) && (typeof(IStateEntity).IsAssignableFrom(declaring) || typeof(ITenantEntity).IsAssignableFrom(declaring))
            && !(method.Name == "get_TenantId" && typeof(Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition).IsAssignableFrom(declaring)))
        {
            return $"decides by create mode (reads {name})";
        }

        if (IsDynamic(declaring))
        {
            return "uses dynamic";
        }

        if (declaring == typeof(MarkupString))
        {
            return "writes runtime markup (MarkupString)";
        }

        if (declaring != typeof(RenderTreeBuilder))
        {
            return null;
        }

        return method switch
        {
            MethodInfo { Name: nameof(RenderTreeBuilder.OpenComponent), IsGenericMethod: true } open when IsRawInput(open.GetGenericArguments()[0]) =>
                $"binds a raw input ({Short(open.GetGenericArguments()[0])})",
            MethodInfo { Name: nameof(RenderTreeBuilder.OpenComponent), IsGenericMethod: false } => "opens a component by a runtime type",
            MethodInfo { Name: nameof(RenderTreeBuilder.AddMultipleAttributes) } => "splats attributes it builds itself (AddMultipleAttributes)",
            MethodInfo { Name: nameof(RenderTreeBuilder.AddAttribute) } when method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrame)) =>
                "copies a render frame in (AddAttribute)",
            _ => null,
        };
    }

    private static IEnumerable<string> BuiltNames(MethodBase method)
    {
        foreach (var (called, literals) in IlScan.LiteralArguments(method))
        {
            if (called.DeclaringType != typeof(RenderTreeBuilder) || !namedArguments.TryGetValue(called.Name, out var parameter))
            {
                continue;
            }

            var position = Array.FindIndex(called.GetParameters(), candidate => candidate.Name == parameter && candidate.ParameterType == typeof(string));
            if (position >= 0 && literals[position + 1] is null)
            {
                yield return $"builds the {parameter} of {called.Name} at run time";
            }
        }
    }

    private static bool IsDynamic(Type? type) =>
        type is not null && (type.Namespace?.StartsWith("Microsoft.CSharp.RuntimeBinder", StringComparison.Ordinal) == true
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Runtime.CompilerServices.CallSite<>))
            || type == typeof(System.Runtime.CompilerServices.CallSite));

    private static bool IsRawInput(Type component)
    {
        if (component == typeof(DynamicComponent))
        {
            return true;
        }

        for (var type = component; type is not null; type = type.BaseType)
        {
            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            if (definition.FullName is "Radzen.FormComponent`1" or "Microsoft.AspNetCore.Components.Forms.InputBase`1")
            {
                return true;
            }
        }

        return component.GetInterfaces().Any(contract => contract.FullName == "Radzen.IRadzenFormComponent");
    }

    private static string Short(Type type) => type.Name.Split('`')[0];

    private static string? CheckMarkup(string text)
    {
        if (accessAttributes.Contains(text))
        {
            return $"writes the access into its markup ({text})";
        }

        if (inputElements.Contains(text))
        {
            return $"writes an input element of its own ({text})";
        }

        return text.Contains('<', StringComparison.Ordinal) && (MarkupInput().IsMatch(text) || MarkupAccess().IsMatch(text))
            ? $"writes the access into its static markup: {text}"
            : null;
    }

    [GeneratedRegex(@"<\s*(input|select|textarea)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupInput();

    [GeneratedRegex(@"<[^>]*\s(disabled|readonly|required|hidden|inert|aria-disabled|aria-readonly|aria-required)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupAccess();

    private static IEnumerable<Type> Chain(Type type)
    {
        for (var current = type; current is not null && IsStaffora(current) && !IsFoundation(current); current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool IsBody(Type type) =>
        !type.IsAbstract && type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IStateEntityDialog<>));

    private static bool IsContext(Type type) => !type.IsAbstract && typeof(StateEntityDialogContext).IsAssignableFrom(type) && !IsFoundation(type);

    private static bool IsStaffora(Type type) => type.Assembly.GetName().Name?.StartsWith("Staffora.", StringComparison.Ordinal) == true;

    private static bool IsFoundation(Type type)
    {
        var assembly = type.Assembly.GetName().Name;
        if (assembly is "Staffora.Services" or "Staffora.Data")
        {
            return true;
        }

        if (assembly == "Staffora.Core")
        {
            return type.Namespace is { } name && (name == "Staffora.Core.Workflow" || name.StartsWith("Staffora.Core.Workflow.", StringComparison.Ordinal));
        }

        if (assembly != "Staffora.Blazor")
        {
            return false;
        }

        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (foundation.Contains(current.IsGenericType ? current.GetGenericTypeDefinition() : current))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Type> Scanned() =>
        new[] { typeof(StateEntityDialogContext).Assembly, typeof(StateFieldScanTests).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace != SamplesNamespace && !type.Name.Contains('<', StringComparison.Ordinal));

    private static string Describe(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";
}
