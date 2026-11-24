using Staffora.Core.Annotations;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Test.Unit.Workflow.Samples;

public enum ParentState
{
    Draft = 0,
    Active = 1,
    Canceled = 2,
    [DeleteState] Deleted = 3,
}

public enum ChildState
{
    Draft = 0,
    Active = 1,
    Declined = 2,
    Canceled = 3,
    [DeleteState] Deleted = 4,

    Imported = 5,
}

public enum GrandchildState
{
    Open = 0,
    Canceled = 1,
    [DeleteState] Deleted = 2,
}

[SampleParentWorkflow]
public sealed class SampleParent : TenantStateEntityBase<ParentState>, Staffora.Core.Entities.Comments.ICommentedEntity
{
    public string? Name { get; set; }

    public string? Notes { get; set; }

    public List<SampleChild> Children { get; set; } = [];
}

[SampleChildWorkflow]
public sealed class SampleChild : TenantStateEntityBase<ChildState>
{
    public Guid ParentId { get; set; }

    public SampleParent? Parent { get; set; }

    public bool IsPinned { get; set; }

    public List<SampleGrandchild> Grandchildren { get; set; } = [];
}

[SampleGrandchildWorkflow]
public sealed class SampleGrandchild : TenantStateEntityBase<GrandchildState>
{
    public Guid ChildId { get; set; }

    public SampleChild? Child { get; set; }
}

public sealed class SampleParentWorkflowAttribute : WorkflowAttribute
{
    public const string AreaChildren = "Children";
    public const string AreaAttributes = "Attributes";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<ParentState, SampleParent>();

        workflow.AddState(ParentState.Draft)
            .ConfigureFields(f => f.Field(e => e.Name, FieldAccessFlags.Required));

        workflow.AddState(ParentState.Active)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.Notes, FieldAccessFlags.Required)
                .Area(AreaChildren, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Children).Field(e => e.Notes))
                .Area(AreaAttributes, FieldAccessFlags.None));

        workflow.AddState(ParentState.Canceled)
            .ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly))
            .Cascade(e => e.Children, ChildState.Canceled, ChildState.Declined);

        workflow.AddState(ParentState.Deleted)
            .ConfigureFields(f => f.Default(FieldAccessFlags.None))
            .Cascade(e => e.Children, ChildState.Deleted);

        workflow.AddTransition(new("parent-save", ParentState.Draft, ParentState.Draft)
        {
            Label = "Save",
            Description = "Save the draft",
            Icon = "save",
            SortOrder = 20,
        });

        workflow.AddTransition(new("parent-activate", ParentState.Draft, ParentState.Active)
        {
            Description = "Put the parent in use",
            RequiredPermission = RolePermission.TenantView,
            ShowWhenBlocked = true,
            SortOrder = 30,
        })
        .AddCondition(new RequiredFieldCondition<ParentState, SampleParent>(e => e.Name))
        .AddCondition(new DelegateCondition<ParentState, SampleParent>("Has at least one child", e => e.Children.Count > 0, nameof(SampleParent.Children)));

        workflow.AddTransition(new("parent-delete", ParentState.Draft, ParentState.Deleted)
        {
            Description = "Delete the draft with its children",
            ConfirmationMessage = "Delete this parent and everything below it?",
            Placement = TransitionPlacement.Leading,
            SortOrder = 10,
        });

        workflow.AddTransition(new("parent-cancel", ParentState.Active, ParentState.Canceled)
        {
            Description = "Cancel the parent and its children",
            SortOrder = 10,
        });

        workflow.AddTransition(new("parent-sync-cancel", ParentState.Active, ParentState.Canceled)
        {
            Description = "Canceled by the other side",
            IsSystemDriven = true,
        });

        return workflow;
    }
}

public sealed class SampleChildWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<ChildState, SampleChild>();

        workflow.AddState(ChildState.Draft);
        workflow.AddState(ChildState.Active);
        workflow.AddState(ChildState.Declined);
        workflow.AddState(ChildState.Imported);
        workflow.AddState(ChildState.Canceled).Cascade(e => e.Grandchildren, GrandchildState.Canceled);
        workflow.AddState(ChildState.Deleted).Cascade(e => e.Grandchildren, GrandchildState.Deleted);

        workflow.AddTransition(new("child-activate", ChildState.Draft, ChildState.Active) { Description = "Activate the child" });
        workflow.AddTransition(new("child-decline", ChildState.Draft, ChildState.Declined) { Description = "Decline the child" });
        workflow.AddTransition(new("child-cancel-draft", ChildState.Draft, ChildState.Canceled) { Description = "Cancel the draft child" });
        workflow.AddTransition(new("child-cancel", ChildState.Active, ChildState.Canceled) { Description = "Cancel the child" });
        workflow.AddTransition(new("child-delete", ChildState.Draft, ChildState.Deleted) { Description = "Delete the draft child" })
            .AddCondition(new DelegateCondition<ChildState, SampleChild>("The child is not pinned", e => !e.IsPinned));

        return workflow;
    }
}

public sealed class SampleGrandchildWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<GrandchildState, SampleGrandchild>();

        workflow.AddState(GrandchildState.Open);
        workflow.AddState(GrandchildState.Canceled);
        workflow.AddState(GrandchildState.Deleted);

        workflow.AddTransition(new("grandchild-cancel", GrandchildState.Open, GrandchildState.Canceled) { Description = "Cancel the grandchild" });
        workflow.AddTransition(new("grandchild-delete", GrandchildState.Open, GrandchildState.Deleted) { Description = "Delete the grandchild" });

        return workflow;
    }
}

internal static class SampleTree
{
    public static SampleParent Build(int children = 2, int grandchildrenEach = 2)
    {
        var parent = new SampleParent { Id = Guid.NewGuid(), Name = "Parent" };
        for (var c = 0; c < children; c++)
        {
            var child = new SampleChild { Id = Guid.NewGuid(), Parent = parent, ParentId = parent.Id };
            for (var g = 0; g < grandchildrenEach; g++)
            {
                child.Grandchildren.Add(new SampleGrandchild { Id = Guid.NewGuid(), Child = child, ChildId = child.Id });
            }

            parent.Children.Add(child);
        }

        return parent;
    }
}

public enum LinkState
{
    Open = 0,
    [DeleteState] Deleted = 1,
}

[LinkedWorkflow]
public sealed class LinkedEntity : StateEntityBase<LinkState>
{
    public List<LinkedEntity> Links { get; set; } = [];
}

public sealed class LinkedWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<LinkState, LinkedEntity>();

        workflow.AddState(LinkState.Open);
        workflow.AddState(LinkState.Deleted).Cascade(e => e.Links, LinkState.Deleted);

        workflow.AddTransition(new("linked-delete", LinkState.Open, LinkState.Deleted) { Description = "Delete the entity and everything it links to" });

        return workflow;
    }
}
