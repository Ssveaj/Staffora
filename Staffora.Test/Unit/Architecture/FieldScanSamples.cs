using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Components.Common.WorkflowFields;
using Staffora.Blazor.Components.EntityAttributes;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Data;
using Staffora.Services.Workflow;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Architecture.DialogScanSamples;

public sealed class StateFieldSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<StateFieldTextBox>(0);
        builder.AddComponentParameter(1, nameof(StateFieldTextBox.Label), "Name");
        builder.CloseComponent();
    }
}

public sealed class StateBranchSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context.Entity?.State == ParentState.Draft)
        {
            builder.OpenComponent<StateFieldTextBox>(0);
            builder.CloseComponent();
        }
    }
}

public sealed class StoredStateSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    private bool draft;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void OnParametersSet() => this.draft = Context.State is ParentState.Draft;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (this.draft)
        {
            builder.OpenComponent<StateFieldTextBox>(0);
            builder.CloseComponent();
        }
    }
}

public sealed class FieldConfigSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool NameIsEditable => Context.Workflow.GetFieldConfig(Context.Entity!).IsEditable(entity => entity.Name);
}

public static class SampleFieldChecks
{
    public static bool IsLocked(SampleParent parent) => parent.State != ParentState.Draft;
}

public sealed class HelperStateSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Locked => SampleFieldChecks.IsLocked(Context.Entity!);
}

public sealed class LockableSampleEntity : TenantStateEntityBase<ParentState>
{
    public bool IsLocked => State != ParentState.Draft;
}

public sealed class EntityPropertySampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    private readonly LockableSampleEntity entity = new();

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Locked => this.entity.IsLocked;
}

public sealed class ExpressionSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    private static readonly Expression<Func<SampleParent, ParentState>> ReadState = entity => entity.State;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Draft => ReadState.Compile()(Context.Entity!) == ParentState.Draft;
}

public sealed class FlagsSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Editable(object access) => access is FieldAccessFlags flags && flags.HasFlag(FieldAccessFlags.Editable);
}

public sealed class ContextAccessSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Editable => Context.FieldAccessOf(nameof(SampleParent.Name)) != 0;
}

public sealed class CreateFlagSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Saved => !Context.IsCreate;
}

public sealed class EmptyIdSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Saved => Context.Entity!.Id != Guid.Empty;
}

public sealed class DisabledParameterSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<AttributeValueField>(0);
        builder.AddComponentParameter(1, nameof(AttributeValueField.Disabled), true);
        builder.CloseComponent();
    }
}

public sealed class RawInputSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<RadzenTextBox>(0);
        builder.CloseComponent();
    }
}

public sealed class RawElementSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "readonly", true);
        builder.CloseElement();
    }
}

public sealed class MarkupSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddMarkupContent(0, "<label>Name <input type=\"text\" readonly></label>");
}

[DialogRoute("samplefieldaccess")]
public sealed class FieldAccessSampleContext : StateEntityDialogContextBase<ParentState, SampleParent>
{
    private readonly MatchingSampleService service;

    public FieldAccessSampleContext(MatchingSampleService service, Guid? id) : base(id, SampleParentWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    public bool NameIsEditable => Workflow.GetFieldConfig(Entity!).GetFieldAccess(entity => entity.Name).HasFlag(FieldAccessFlags.Editable);

    protected override Task<Result<DetachedEntityUpdater<SampleParent>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadSampleParentUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<SampleParent> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateSampleParentAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetSampleParentHistoryAsync(entityId, query, cancellationToken);
}

public sealed class EditChannelSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    private int? shownVersion;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void OnParametersSet()
    {
        if (this.shownVersion != Context.Version)
        {
            this.shownVersion = Context.Version;
            Context.DisplayName = Context.Entity!.Name;
            Context.TransitionFilter = transition => transition.Label != "Archive";
            Context.FooterDisabled = Context.Lifetime.IsCancellationRequested;
            Context.NotifyChanged();
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "sample-body");
        builder.OpenComponent<StateFieldTextBox>(2);
        builder.AddComponentParameter(3, nameof(StateFieldTextBox.Label), "Name");
        builder.CloseComponent();
        builder.CloseElement();
        Context.NotifyEdited();
    }
}

public sealed class RequirementSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Required => Context.RequirementOf(typeof(object)).IsRequired;
}

public sealed class EntityIdSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context.EntityId is null)
        {
            builder.OpenComponent<StateFieldTextBox>(0);
            builder.CloseComponent();
        }
    }
}

public sealed class UpdaterNullSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context.Updater is null)
        {
            builder.OpenComponent<StateFieldTextBox>(0);
            builder.CloseComponent();
        }
    }
}

public sealed class UpdaterSnapshotSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Equals(Context.Updater?.OriginalValue(nameof(SampleParent.State)), ParentState.Draft))
        {
            builder.OpenComponent<StateFieldTextBox>(0);
            builder.CloseComponent();
        }
    }
}

public sealed class UrlSegmentSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "style", Context.UrlSegment is null ? "display: none" : null);
        builder.OpenComponent<StateFieldTextBox>(2);
        builder.CloseComponent();
        builder.CloseElement();
    }
}

public sealed class TitleSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool New => Context.Title == Context.CreateTitle;
}

public sealed class HasSavedSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Saved => Context.HasSaved;
}

public sealed class DefaultIdSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool New => Context.Entity!.Id == default;
}

public sealed class AuditColumnSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool New => Context.Entity!.Created == default;
}

public sealed class InTransitionSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Locked => Context.Entity!.IsInTransition || Context.IsBusy;
}

public sealed class HelperNamespaceSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Draft => Staffora.Blazor.Helper.ScanSamples.NamespaceHelper.IsDraft(Context.Entity!);
}

public sealed class DialogNamespaceSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Draft => Staffora.Blazor.Components.Common.Dialogs.StateEntity.ScanSamples.DialogNamespaceHelper.IsDraft(Context.Entity!);
}

public sealed class SampleInstanceHelper
{
    public bool IsDraft(SampleParent parent) => parent.State == ParentState.Draft;
}

public sealed class InstanceHelperSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public SampleInstanceHelper Helper { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Draft => Helper.IsDraft(Context.Entity!);
}

public sealed class SampleDraftGuard
{
    public SampleDraftGuard(SampleParent parent)
    {
        if (parent.State != ParentState.Draft)
        {
            throw new InvalidOperationException("Not a draft.");
        }
    }
}

public sealed class ConstructorHelperSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Draft
    {
        get
        {
            try
            {
                _ = new SampleDraftGuard(Context.Entity!);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}

public interface ISampleDraftCheck
{
    bool IsDraft(SampleParent parent);
}

public sealed class SampleDraftCheck : ISampleDraftCheck
{
    public bool IsDraft(SampleParent parent) => parent.State == ParentState.Draft;
}

public sealed class InterfaceHelperSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public ISampleDraftCheck Check { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Draft => Check.IsDraft(Context.Entity!);
}

public sealed class SampleDraftComparer : IEqualityComparer<SampleParent>
{
    public bool Equals(SampleParent? x, SampleParent? y) => x?.State == ParentState.Draft && y is null;

    public int GetHashCode(SampleParent obj) => 0;
}

public sealed class FrameworkInterfaceSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Inject] public IEqualityComparer<SampleParent> Comparer { get; set; } = null!;

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Draft => Comparer.Equals(Context.Entity, null);
}

public sealed class ForeachSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public List<string> Names { get; } = [];

    public int Count()
    {
        var count = 0;
        foreach (var name in Names)
        {
            count += name.Length;
        }

        return count;
    }
}

public sealed class InterfaceForeachSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    [Parameter] public IReadOnlyList<SampleOccurrence> Occurrences { get; set; } = [];

    public int Count()
    {
        var count = 0;
        var occurrences = Occurrences;
        foreach (var occurrence in occurrences)
        {
            count += occurrence.Minutes;
        }

        return count;
    }
}

public sealed record SampleOccurrence(int Minutes);

public sealed class InterfaceDisposeSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    [Parameter] public IDisposable? Held { get; set; }

    public void Release() => Held?.Dispose();
}

public static class SampleDelegates
{
    public static readonly Func<SampleParent, bool> IsDraft = parent => parent.State == ParentState.Draft;
}

public sealed class StaticDelegateSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Draft => SampleDelegates.IsDraft(Context.Entity!);
}

public static class SampleGenericChecks
{
    public static bool Is<TState>(IStateEntity<TState> entity, TState state) where TState : struct, Enum => EqualityComparer<TState>.Default.Equals(entity.State, state);
}

public sealed class GenericHelperSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    public bool Draft => SampleGenericChecks.Is(Context.Entity!, ParentState.Draft);
}

public sealed class SampleWrapperInput : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<RadzenTextBox>(0);
        builder.CloseComponent();
    }
}

public sealed class WrapperComponentSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<SampleWrapperInput>(0);
        builder.CloseComponent();
    }
}

public sealed class DynamicComponentSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<DynamicComponent>(0);
        builder.AddComponentParameter(1, nameof(DynamicComponent.Type), typeof(RadzenTextBox));
        builder.CloseComponent();
    }
}

public sealed class RuntimeTypeSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, typeof(RadzenTextBox));
        builder.CloseComponent();
    }
}

public sealed class BuiltAttributeSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    private static readonly string Suffix = "only";

    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "fieldset");
        builder.AddAttribute(1, string.Concat("dis", "abled"), Context.HasSaved);
        builder.AddMultipleAttributes(2, new Dictionary<string, object> { ["read" + Suffix] = true });
        builder.CloseElement();
    }
}

public sealed class BuiltElementSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, string.Concat("inp", "ut"));
        builder.CloseElement();
        builder.AddMarkupContent(1, string.Concat("<texta", "rea>x</textarea>"));
    }
}

public sealed class RuntimeMarkupSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, new MarkupString(string.Concat("<inp", "ut read", "only>")));
}

public sealed class BuiltParameterSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<AttributeValueField>(0);
        builder.AddComponentParameter(1, string.Concat("Dis", "abled"), true);
        builder.CloseComponent();
    }
}

public sealed class DynamicSampleBody : ComponentBase, IStateEntityDialog<MatchingSampleContext>
{
    [Parameter] public MatchingSampleContext Context { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        dynamic entity = Context.Entity!;
        if (entity.State == ParentState.Draft)
        {
            builder.OpenComponent<StateFieldTextBox>(0);
            builder.CloseComponent();
        }
    }
}
