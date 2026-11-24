using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public abstract class CustomFieldWorkflowAttribute<TDefinition> : WorkflowAttribute
    where TDefinition : class, IEntityAttributeDefinition
{
    public const string CreateStep = "field-create";

    public const string SaveDraftStep = "field-save-draft";

    public const string ActivateStep = "field-activate";

    public const string SaveActiveStep = "field-save-active";

    public const string DeactivateStep = "field-deactivate";

    public const string SaveInactiveStep = "field-save-inactive";

    public const string ReactivateStep = "field-reactivate";

    public const string ChoiceWithoutOptions = "A choice field needs at least one option that is not retired, and every option a label";

    public const string YesOrNoNotRequired = "A yes-or-no field is not required: a box left unticked reads as no";

    private const int Back = 10;
    private const int Neutral = 20;
    private const int Forward = 30;

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<CustomFieldState, TDefinition>();

        workflow.AddState(CustomFieldState.Create).ConfigureFields(fields => Fields(fields, creating: true));
        workflow.AddState(CustomFieldState.Draft).ConfigureFields(fields => Fields(fields, creating: false));
        workflow.AddState(CustomFieldState.Active).ConfigureFields(fields => Fields(fields, creating: false));
        workflow.AddState(CustomFieldState.Inactive).ConfigureFields(fields => Fields(fields, creating: false));

        Saved(workflow.AddTransition(new(CreateStep, CustomFieldState.Create, CustomFieldState.Draft)
        {
            Label = "Create",
            Description = "Save the new field as a draft: no tenant sees it until it is activated",
            Icon = "add_task",
            RequiresPlatformOwner = true,
            SortOrder = Forward,
        }));

        Saved(workflow.AddTransition(new(SaveDraftStep, CustomFieldState.Draft, CustomFieldState.Draft)
        {
            Label = "Save",
            Description = "Save the draft's name, options and settings. It stays a draft that no tenant sees",
            Icon = "save",
            RequiresPlatformOwner = true,
            SortOrder = Neutral,
        }));

        Saved(workflow.AddTransition(new(ActivateStep, CustomFieldState.Draft, CustomFieldState.Active)
        {
            Label = "Activate",
            Description = "Put the field in use: it shows in the dialogs of the records it applies to, in its tenant or in every tenant",
            Icon = "check_circle",
            RequiresPlatformOwner = true,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        }));

        Saved(workflow.AddTransition(new(SaveActiveStep, CustomFieldState.Active, CustomFieldState.Active)
        {
            Label = "Save",
            Description = "Save the field's name, options and settings. The records keep their values",
            Icon = "save",
            RequiresPlatformOwner = true,
            SortOrder = Neutral,
        }));

        Saved(workflow.AddTransition(new(DeactivateStep, CustomFieldState.Active, CustomFieldState.Inactive)
        {
            Label = "Deactivate",
            Description = "Take the field out of use: it leaves every record's dialog and is required nowhere. The values records hold are kept",
            Icon = "visibility_off",
            ConfirmationMessage = "Deactivate this field? It leaves every record's dialog and is no longer required. The values records hold are kept, and they come back when the field is activated again.",
            RequiresPlatformOwner = true,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        }));

        Saved(workflow.AddTransition(new(SaveInactiveStep, CustomFieldState.Inactive, CustomFieldState.Inactive)
        {
            Label = "Save",
            Description = "Save the inactive field's name, options and settings. It stays out of use",
            Icon = "save",
            RequiresPlatformOwner = true,
            SortOrder = Neutral,
        }));

        Saved(workflow.AddTransition(new(ReactivateStep, CustomFieldState.Inactive, CustomFieldState.Active)
        {
            Label = "Activate",
            Description = "Put the field back in use: it shows in the records' dialogs again, with the values they kept",
            Icon = "check_circle",
            RequiresPlatformOwner = true,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        }));

        return workflow;
    }

    protected virtual FieldAccessFlags RequiredMarkAccess => FieldAccessFlags.None;

    protected virtual StateFieldBuilder<TDefinition> OwnFields(StateFieldBuilder<TDefinition> fields) => fields;

    private StateFieldBuilder<TDefinition> Fields(StateFieldBuilder<TDefinition> fields, bool creating) => OwnFields(fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.Name, FieldAccessFlags.Required)
        .Field(e => e.TenantId, creating ? FieldAccessFlags.ReadWrite : FieldAccessFlags.ReadOnly)
        .Field(e => e.ValueType, creating ? FieldAccessFlags.Required : FieldAccessFlags.ReadOnly)
        .Field(e => e.Options, FieldAccessFlags.ReadWrite)
        .Field(e => e.IsRequired, RequiredMarkAccess));

    private static void Saved(Transition<CustomFieldState, TDefinition> transition) => transition
        .AddCondition(new RequiredFieldCondition<CustomFieldState, TDefinition>(e => e.Name))
        .AddCondition(new DelegateCondition<CustomFieldState, TDefinition>(ChoiceWithoutOptions, OptionsCanBeSaved))
        .AddCondition(new DelegateCondition<CustomFieldState, TDefinition>(YesOrNoNotRequired, field => !(field.IsRequired && field.ValueType == AttributeValueType.Boolean)));

    private static bool OptionsCanBeSaved(TDefinition field) =>
        field.ValueType != AttributeValueType.Choice
        || (field.Options.Any(option => !option.IsRetired) && field.Options.All(option => !string.IsNullOrWhiteSpace(option.Label)));
}

public sealed class UserFieldWorkflowAttribute : CustomFieldWorkflowAttribute<ApplicationUserAttributeDefinition>;

public sealed class UnitFieldWorkflowAttribute : CustomFieldWorkflowAttribute<OrganizationUnitAttributeDefinition>;

public sealed class JobFieldWorkflowAttribute : CustomFieldWorkflowAttribute<JobAttributeDefinition>
{
    protected override FieldAccessFlags RequiredMarkAccess => FieldAccessFlags.ReadWrite;

    protected override StateFieldBuilder<JobAttributeDefinition> OwnFields(StateFieldBuilder<JobAttributeDefinition> fields) =>
        fields.Field(e => e.Audience, FieldAccessFlags.ReadWrite);
}
