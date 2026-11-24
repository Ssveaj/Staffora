using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Test.Integration.Infrastructure;

public enum TestParentState
{
    [Display(Name = "Draft")]
    [Description("Being prepared; name and notes can be edited")]
    [Icon("edit_note")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Draft = 0,

    [Display(Name = "Active")]
    [Description("In use; only the notes can be edited")]
    [Icon("check_circle")]
    [StateStyling("#4CAF50")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Canceled")]
    [Description("Canceled together with its children")]
    [Icon("cancel")]
    [StateStyling("#757575")]
    [EntityStateCategory(StateCategory.Canceled)]
    Canceled = 2,

    [Display(Name = "Deleted")]
    [Description("Removed together with its children and grandchildren")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [EntityStateCategory(StateCategory.Archived)]
    [DeleteState]
    Deleted = 3,

    [Display(Name = "Confirmed")]
    [Description("Confirmed by the system; the notes and custom fields can still be edited")]
    [Icon("verified")]
    [StateStyling("#00897B")]
    [EntityStateCategory(StateCategory.Open)]
    Confirmed = 4,
}

public enum TestChildState
{
    [Display(Name = "Draft")]
    [Description("Being prepared")]
    [Icon("edit_note")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Draft = 0,

    [Display(Name = "Active")]
    [Description("In use")]
    [Icon("check_circle")]
    [StateStyling("#4CAF50")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Canceled")]
    [Description("Canceled with its parent")]
    [Icon("cancel")]
    [StateStyling("#757575")]
    [EntityStateCategory(StateCategory.Canceled)]
    Canceled = 2,

    [Display(Name = "Deleted")]
    [Description("Removed with its parent")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [EntityStateCategory(StateCategory.Archived)]
    [DeleteState]
    Deleted = 3,
}

public enum TestGrandchildState
{
    [Display(Name = "Open")]
    [Description("In use")]
    [Icon("radio_button_unchecked")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Open = 0,

    [Display(Name = "Canceled")]
    [Description("Canceled with its child")]
    [Icon("cancel")]
    [StateStyling("#757575")]
    [EntityStateCategory(StateCategory.Canceled)]
    Canceled = 1,

    [Display(Name = "Deleted")]
    [Description("Removed with its child")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [EntityStateCategory(StateCategory.Archived)]
    [DeleteState]
    Deleted = 2,
}

[TestParentWorkflow]
public class TestParent : TenantStateEntityBase<TestParentState>, ICommentedEntity
{
    public const string FailInAnAction = "fail in an action";

    public const string FailAtTheDatabase = "fail at the database";

    public const string FailInACondition = "fail in a condition";

    public const string FailInTheSavesCondition = "fail in the save's condition";

    public const string AsksForAComment = "ask for a comment";

    public const string TakesNoActiveChildren = "take no active children";

    public const string RefusedBySave = "refused by the save";

    public const string NotesTheSaveTakes = "The notes are ones the save takes";

    public const RolePermission FilePermission = RolePermission.UsersView;

    public const string ActivatedMessage = "test-parent-activated";

    public static readonly MessageType Activated = new(ActivatedMessage, MessageCategory.Account, "Test parent activated", "Name", "ParentId");

    public static readonly MessageTemplate ActivatedTemplate = new("{{Name}} is active", "The parent {{Name}} was activated.", "?dlg=testparent-{{ParentId}}");

    public string Name { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public DateTime? DueAt { get; set; }

    public int? Priority { get; set; }

    public DateOnly? StartsOn { get; set; }

    public ICollection<TestChild> Children { get; set; } = [];

    public ICollection<TestParentLine> Lines { get; set; } = [];

    [Display(Name = "Custom fields")]
    public ICollection<TestParentAttribute> Attributes { get; set; } = [];
}

public class TestParentLine : ITenantEntity
{
    public Guid ParentId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public DateTime Created { get; set; }

    public Guid CreatedById { get; set; }

    public DateTime? Modified { get; set; }

    public Guid? ModifiedById { get; set; }

    public Guid? ImpersonatedById { get; set; }
}

[TestChildWorkflow]
public class TestChild : TenantStateEntityBase<TestChildState>
{
    public Guid ParentId { get; set; }

    public TestParent? Parent { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsPinned { get; set; }

    public ICollection<TestGrandchild> Grandchildren { get; set; } = [];
}

[TestGrandchildWorkflow]
public class TestGrandchild : TenantStateEntityBase<TestGrandchildState>
{
    public const string FailWhenDeleted = "fail when deleted";

    public const string DeletedMessage = "test-grandchild-deleted";

    public const string HoldsItsChildOpen = "hold its child open";

    public const string NothingHoldsTheChildOpen = "Nothing holds the child open";

    public Guid ChildId { get; set; }

    public TestChild? Child { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class TestParentWorkflowAttribute : WorkflowAttribute
{
    public const string AreaRows = "Rows";

    public const string AreaPlanning = "Planning";

    public const string AreaAttributes = WorkflowAreas.Attributes;

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<TestParentState, TestParent>();

        workflow.AddState(TestParentState.Draft)
            .ConfigureFields(f => f
                .Field(e => e.Name, FieldAccessFlags.Required)
                .Field(e => e.Notes, FieldAccessFlags.ReadWrite)
                .Area(AreaRows, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Lines).Field(e => e.Children))
                .Field(e => e.Lines, FieldAccessFlags.Required)
                .Area(AreaPlanning, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Priority).Field(e => e.StartsOn))
                .Area(AreaAttributes, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Attributes)));

        workflow.AddState(TestParentState.Active)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.Notes, FieldAccessFlags.ReadWrite)
                .Area(AreaRows, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Lines).Field(e => e.Children))
                .Area(AreaPlanning, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Priority).Field(e => e.StartsOn))
                .Area(AreaAttributes, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Attributes)));

        workflow.AddState(TestParentState.Canceled)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.Notes, FieldAccessFlags.None)
                .Area(AreaRows, FieldAccessFlags.None, a => a.Field(e => e.Lines).Field(e => e.Children))
                .Area(AreaPlanning, FieldAccessFlags.None, a => a.Field(e => e.Priority).Field(e => e.StartsOn))
                .Area(AreaAttributes, FieldAccessFlags.None, a => a.Field(e => e.Attributes)))
            .Cascade(e => e.Children, TestChildState.Canceled);

        workflow.AddState(TestParentState.Deleted)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.None)
                .Area(AreaPlanning, FieldAccessFlags.None, a => a.Field(e => e.Priority).Field(e => e.StartsOn))
                .Area(AreaAttributes, FieldAccessFlags.None, a => a.Field(e => e.Attributes)))
            .Cascade(e => e.Children, TestChildState.Deleted);

        workflow.AddState(TestParentState.Confirmed)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.Notes, FieldAccessFlags.ReadWrite)
                .Area(AreaRows, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Lines).Field(e => e.Children))
                .Area(AreaPlanning, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Priority).Field(e => e.StartsOn))
                .Area(AreaAttributes, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Attributes)));

        workflow.AddTransition(new("test-parent-save", TestParentState.Draft, TestParentState.Draft)
        {
            Label = "Save",
            Description = "Save the draft",
            SortOrder = 20,
            IsSave = true,
        })
        .AddCondition(new RequiredFieldCondition<TestParentState, TestParent>(e => e.Name))
        .AddCondition(new DelegateCondition<TestParentState, TestParent>("The notes do not break the save", e => e.Notes == TestParent.FailInTheSavesCondition ? throw new InvalidOperationException("The save's condition failed.") : true))
        .AddCondition(new DelegateCondition<TestParentState, TestParent>(TestParent.NotesTheSaveTakes, e => e.Notes != TestParent.RefusedBySave));

        workflow.AddTransition(new("test-parent-file", TestParentState.Draft, TestParentState.Draft)
        {
            Label = "File",
            Description = "Save the draft and file it",
            RequiredPermission = TestParent.FilePermission,
            SortOrder = 21,
        })
        .AddAction(new DelegateAction<TestParentState, TestParent>(context =>
        {
            if (context.Entity.Notes == TestParent.FailInAnAction)
            {
                throw new InvalidOperationException("The filing failed in its action.");
            }
        }));

        workflow.AddTransition(new("test-parent-stamp", TestParentState.Draft, TestParentState.Draft)
        {
            Label = "Stamp",
            Description = "Save the draft with the platform's stamp",
            RequiresPlatformOwner = true,
            SortOrder = 22,
        });

        workflow.AddTransition(new("test-parent-annotate", TestParentState.Draft, TestParentState.Draft)
        {
            Label = "Annotate",
            Description = "Save the draft with a comment",
            CommentPrompt = new("Annotate the parent") { Message = "What is the note?" },
            SortOrder = 23,
        })
        .AddCondition(new DelegateCondition<TestParentState, TestParent>("The notes ask for a comment", e => e.Notes == TestParent.AsksForAComment));

        workflow.AddTransition(new("test-parent-activate", TestParentState.Draft, TestParentState.Active)
        {
            Description = "Put the parent in use",
            ShowWhenBlocked = true,
            SortOrder = 30,
        })
        .AddCondition(new RequiredFieldCondition<TestParentState, TestParent>(e => e.Name))
        .AddCondition(new DelegateCondition<TestParentState, TestParent>("The notes do not break the condition", e => e.Notes == TestParent.FailInACondition ? throw new InvalidOperationException("The activation's condition failed.") : true))
        .AddCondition(new RequiredAttributesCondition<TestParentState, TestParent, TestParentAttribute>(e => e.Attributes))
        .AddAction(new DelegateAction<TestParentState, TestParent>(Activate));

        workflow.AddTransition(new("test-parent-confirm", TestParentState.Active, TestParentState.Confirmed)
        {
            Description = "Confirm an active parent; the system's step",
            IsSystemDriven = true,
            SortOrder = 40,
        })
        .AddCondition(new RequiredAttributesCondition<TestParentState, TestParent, TestParentAttribute>(e => e.Attributes));

        workflow.AddTransition(new("test-parent-save-confirmed", TestParentState.Confirmed, TestParentState.Confirmed)
        {
            Label = "Save",
            Description = "Save the confirmed parent",
            SortOrder = 20,
            IsSave = true,
        });

        workflow.AddTransition(new("test-parent-cancel", TestParentState.Active, TestParentState.Canceled)
        {
            Description = "Cancel the parent and its children",
            CommentPrompt = new("Cancel the parent") { Message = "Why is the parent canceled?" },
            RequiredPermission = RolePermission.SettingsEdit,
            SortOrder = 10,
        })
        .AddAction(new DelegateAction<TestParentState, TestParent>(context =>
        {
            if (context.Entity.Notes == TestParent.FailAtTheDatabase)
            {
                context.Entity.Name = new string('x', 201);
            }
        }));

        workflow.AddTransition(new("test-parent-reopen", TestParentState.Canceled, TestParentState.Draft)
        {
            Description = "Reopen a canceled parent; a platform owner's step",
            RequiresPlatformOwner = true,
            Placement = TransitionPlacement.Leading,
            SortOrder = 10,
        });

        workflow.AddTransition(new("test-parent-restore", TestParentState.Canceled, TestParentState.Draft)
        {
            Description = "Restore a canceled parent; the system's step",
            IsSystemDriven = true,
            SortOrder = 20,
        });

        workflow.AddTransition(new("test-parent-delete", TestParentState.Draft, TestParentState.Deleted)
        {
            Description = "Delete the draft with its children and grandchildren",
            ConfirmationMessage = "Delete this parent and everything below it?",
            Placement = TransitionPlacement.Leading,
            SortOrder = 10,
        });

        return workflow;
    }

    private static void Activate(WorkflowContext<TestParentState, TestParent> context)
    {
        var parent = context.Entity;
        var notice = MessageCatalogue.Render(TestParent.Activated, TestParent.ActivatedTemplate, new Dictionary<string, string?> { ["Name"] = parent.Name, ["ParentId"] = parent.Id.ToString("D") });
        context.OutgoingMessages.Add(new OutgoingMessage
        {
            MessageType = TestParent.ActivatedMessage,
            Category = TestParent.Activated.Category,
            EntityType = nameof(TestParent),
            EntityId = parent.Id,
            Subject = notice.Subject,
            Text = notice.Text,
            Link = notice.Link,
            RecipientUserIds = [parent.CreatedById],
        });
        if (context.ServiceProvider?.GetService(typeof(CommitProbe)) is CommitProbe probe)
        {
            context.AfterCommit.Add(cancellationToken => probe.RecordAsync("after commit", parent.Id, cancellationToken));
        }

        switch (parent.Notes)
        {
            case TestParent.FailInAnAction:
                throw new InvalidOperationException("The activation failed in its action.");
            case TestParent.FailAtTheDatabase:
                parent.Name = new string('x', 201);
                break;
        }
    }
}

public sealed class TestChildWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<TestChildState, TestChild>();

        workflow.AddState(TestChildState.Draft);
        workflow.AddState(TestChildState.Active).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddState(TestChildState.Canceled).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly)).Cascade(e => e.Grandchildren, TestGrandchildState.Canceled);
        workflow.AddState(TestChildState.Deleted).Cascade(e => e.Grandchildren, TestGrandchildState.Deleted);

        workflow.AddTransition(new("test-child-activate", TestChildState.Draft, TestChildState.Active) { Description = "Activate the child" })
            .AddCondition(new DelegateCondition<TestChildState, TestChild>("The parent is not canceled", e => e.Parent!.State != TestParentState.Canceled, nameof(TestChild.Parent)))
            .AddCondition(new DelegateCondition<TestChildState, TestChild>("The parent takes active children", e => e.Parent!.Notes != TestParent.TakesNoActiveChildren, nameof(TestChild.Parent)));

        workflow.AddTransition(new("test-child-cancel-draft", TestChildState.Draft, TestChildState.Canceled) { Description = "Cancel the draft child" })
            .AddCondition(new DelegateCondition<TestChildState, TestChild>(TestGrandchild.NothingHoldsTheChildOpen, e => e.Grandchildren.All(grandchild => grandchild.Name != TestGrandchild.HoldsItsChildOpen), nameof(TestChild.Grandchildren)));
        workflow.AddTransition(new("test-child-cancel", TestChildState.Active, TestChildState.Canceled) { Description = "Cancel the child" });
        workflow.AddTransition(new("test-child-delete", TestChildState.Draft, TestChildState.Deleted) { Description = "Delete the draft child with its grandchildren" })
            .AddCondition(new DelegateCondition<TestChildState, TestChild>("The child is not pinned", e => !e.IsPinned));

        return workflow;
    }
}

public sealed class TestGrandchildWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<TestGrandchildState, TestGrandchild>();

        workflow.AddState(TestGrandchildState.Open);
        workflow.AddState(TestGrandchildState.Canceled);
        workflow.AddState(TestGrandchildState.Deleted);

        workflow.AddTransition(new("test-grandchild-cancel", TestGrandchildState.Open, TestGrandchildState.Canceled) { Description = "Cancel the grandchild" })
            .AddCondition(new DelegateCondition<TestGrandchildState, TestGrandchild>(TestGrandchild.NothingHoldsTheChildOpen, e => e.Name != TestGrandchild.HoldsItsChildOpen));
        workflow.AddTransition(new("test-grandchild-delete", TestGrandchildState.Open, TestGrandchildState.Deleted) { Description = "Delete the grandchild" })
            .AddAction(new DelegateAction<TestGrandchildState, TestGrandchild>(FailWhenAsked));

        return workflow;
    }

    private static void FailWhenAsked(WorkflowContext<TestGrandchildState, TestGrandchild> context)
    {
        var grandchild = context.Entity;
        if (grandchild.Name != TestGrandchild.FailWhenDeleted)
        {
            return;
        }

        context.OutgoingMessages.Add(new OutgoingMessage { MessageType = TestGrandchild.DeletedMessage, EntityId = grandchild.Id, RecipientUserIds = [grandchild.CreatedById] });
        if (context.ServiceProvider?.GetService(typeof(CommitProbe)) is CommitProbe probe)
        {
            context.AfterCommit.Add(cancellationToken => probe.RecordAsync("after commit", grandchild.Id, cancellationToken));
        }

        throw new InvalidOperationException("The grandchild's delete failed in its action.");
    }
}

public enum TestFolderState
{
    [Display(Name = "Draft")]
    [Description("Being filled")]
    [Icon("folder_open")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Draft = 0,

    [Display(Name = "Deleted")]
    [Description("Removed with its documents")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [EntityStateCategory(StateCategory.Archived)]
    [DeleteState]
    Deleted = 1,
}

public enum TestDocumentState
{
    [Display(Name = "Draft")]
    [Description("Being written")]
    [Icon("edit_note")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Draft = 0,

    [Display(Name = "Sealed")]
    [Description("Sealed; only a platform owner removes it")]
    [Icon("lock")]
    [StateStyling("#757575")]
    [EntityStateCategory(StateCategory.Done)]
    Sealed = 1,

    [Display(Name = "Deleted")]
    [Description("Removed with its folder")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [EntityStateCategory(StateCategory.Archived)]
    [DeleteState]
    Deleted = 2,

    [Display(Name = "Filed")]
    [Description("Filed: its folder's sealed documents are archived with it")]
    [Icon("inventory_2")]
    [StateStyling("#4CAF50")]
    [EntityStateCategory(StateCategory.Done)]
    Filed = 3,

    [Display(Name = "Archived")]
    [Description("Archived beside a filed document")]
    [Icon("archive")]
    [StateStyling("#757575")]
    [EntityStateCategory(StateCategory.Archived)]
    Archived = 4,
}

[TestFolderWorkflow]
public class TestFolder : TenantStateEntityBase<TestFolderState>
{
    public const RolePermission DeletePermission = RolePermission.RolesDelete;

    public const string SealsItsDocuments = "seal its documents";

    public const string AddsADocument = "add a document";

    public string Name { get; set; } = string.Empty;

    public ICollection<TestDocument> Documents { get; set; } = [];

    [Display(Name = "Custom fields")]
    public ICollection<TestFolderAttribute> Attributes { get; set; } = [];
}

[TestDocumentWorkflow]
public class TestDocument : TenantStateEntityBase<TestDocumentState>
{
    public const RolePermission DeletePermission = RolePermission.OrganizationUnitsDelete;

    public const RolePermission ArchivePermission = RolePermission.CustomersManage;

    public Guid FolderId { get; set; }

    public TestFolder? Folder { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class TestFolderWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<TestFolderState, TestFolder>();

        workflow.AddState(TestFolderState.Draft)
            .ConfigureFields(f => f.Area(WorkflowAreas.Attributes, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Attributes)));
        workflow.AddState(TestFolderState.Deleted)
            .ConfigureFields(f => f.Default(FieldAccessFlags.None).Area(WorkflowAreas.Attributes, FieldAccessFlags.None, a => a.Field(e => e.Attributes)))
            .Cascade(e => e.Documents, TestDocumentState.Deleted);

        workflow.AddTransition(new("test-folder-delete", TestFolderState.Draft, TestFolderState.Deleted)
        {
            Description = "Delete the folder with its documents",
            ConfirmationMessage = "Delete this folder and its documents?",
            RequiredPermission = TestFolder.DeletePermission,
            Placement = TransitionPlacement.Leading,
            SortOrder = 10,
        });

        workflow.AddTransition(new("test-folder-touch-a-stranger", TestFolderState.Draft, TestFolderState.Draft)
        {
            Description = "Name an untracked document as a lock candidate",
            SortOrder = 20,
        })
        .AddAction(new DelegateAction<TestFolderState, TestFolder>(_ => { }, _ => [new TestDocument { Id = Guid.NewGuid() }]));

        workflow.AddTransition(new("test-folder-delete-unchecked", TestFolderState.Draft, TestFolderState.Deleted)
        {
            Description = "Delete the folder after an action changed what its cascade takes",
            ConfirmationMessage = "Delete this folder and its documents?",
            Placement = TransitionPlacement.Leading,
            SortOrder = 10,
        })
        .AddAction(new DelegateAction<TestFolderState, TestFolder>(ChangeWhatTheCascadeTakes));

        workflow.AddTransition(new("test-folder-seal-the-documents", TestFolderState.Draft, TestFolderState.Draft)
        {
            Label = "Seal the documents",
            Description = "Seal the documents by writing their state directly",
            SortOrder = 20,
        })
        .AddAction(new DelegateAction<TestFolderState, TestFolder>(
            context =>
            {
                foreach (var document in context.Entity.Documents)
                {
                    document.State = TestDocumentState.Sealed;
                }
            },
            context => context.Entity.Documents));

        workflow.AddTransition(new("test-folder-name-a-new-document", TestFolderState.Draft, TestFolderState.Draft)
        {
            Description = "Name a new document as a lock candidate",
            SortOrder = 20,
        })
        .AddAction(new DelegateAction<TestFolderState, TestFolder>(
            _ => { },
            context =>
            {
                var document = new TestDocument { Name = "New document" };
                context.Entity.Documents.Add(document);
                return [document];
            }));

        return workflow;
    }

    private static void ChangeWhatTheCascadeTakes(WorkflowContext<TestFolderState, TestFolder> context)
    {
        var folder = context.Entity;
        switch (folder.Name)
        {
            case TestFolder.SealsItsDocuments:
                foreach (var document in folder.Documents)
                {
                    document.State = TestDocumentState.Sealed;
                }

                break;
            case TestFolder.AddsADocument:
                folder.Documents.Add(new TestDocument { Name = "Added by the step" });
                break;
        }
    }
}

public sealed class TestDocumentWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<TestDocumentState, TestDocument>();

        workflow.AddState(TestDocumentState.Draft);
        workflow.AddState(TestDocumentState.Sealed).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddState(TestDocumentState.Deleted).ConfigureFields(f => f.Default(FieldAccessFlags.None));
        workflow.AddState(TestDocumentState.Filed).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly))
            .Cascade(e => e.Folder!.Documents, TestDocumentState.Archived, TestDocumentState.Filed);
        workflow.AddState(TestDocumentState.Archived).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));

        workflow.AddTransition(new("test-document-seal", TestDocumentState.Draft, TestDocumentState.Sealed) { Description = "Seal the document" });
        workflow.AddTransition(new("test-document-delete", TestDocumentState.Draft, TestDocumentState.Deleted)
        {
            Label = "Delete",
            Description = "Delete the draft document",
            RequiredPermission = TestDocument.DeletePermission,
        });
        workflow.AddTransition(new("test-document-purge", TestDocumentState.Sealed, TestDocumentState.Deleted)
        {
            Label = "Purge",
            Description = "Remove a sealed document; a platform owner's step",
            RequiresPlatformOwner = true,
        });
        workflow.AddTransition(new("test-document-file", TestDocumentState.Draft, TestDocumentState.Filed)
        {
            Label = "File",
            Description = "File the document; its folder's other documents are archived",
        });
        workflow.AddTransition(new("test-document-archive-sealed", TestDocumentState.Sealed, TestDocumentState.Archived)
        {
            Label = "Archive",
            Description = "Archive a sealed document",
        });
        workflow.AddTransition(new("test-document-archive-draft", TestDocumentState.Draft, TestDocumentState.Archived)
        {
            Label = "Archive",
            Description = "Archive a draft document",
            RequiredPermission = TestDocument.ArchivePermission,
        });

        return workflow;
    }
}
