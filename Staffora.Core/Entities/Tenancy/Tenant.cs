using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Tenancy;

public enum TenantState
{
    [Display(Name = "Setup")]
    [Description("Created by the platform owner, and only the system owner may sign in to finish setup")]
    [Icon("settings")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Setup = 0,

    [Display(Name = "Active")]
    [Description("In normal use")]
    [Icon("check_circle")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Suspended")]
    [Description("Nobody signs in and nobody may be impersonated, but data and integrations are held")]
    [Icon("block")]
    [StateStyling("#FF9800")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Open)]
    Suspended = 2,

    [Display(Name = "Pending deletion")]
    [Description("Scheduled for removal and read-only for the platform owner")]
    [Icon("delete_sweep")]
    [StateStyling("#F44336")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Archived)]
    PendingDeletion = 3,

    [Display(Name = "Archived")]
    [Description("Kept for the record, with no access")]
    [Icon("archive")]
    [StateStyling("#757575")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Archived)]
    Archived = 4,
}

public static class TenantStates
{
    public static bool HoldsData(TenantState? state) => state is not (TenantState.Setup or TenantState.Active);

    public const string HeldDataReason = "The organization's account is suspended, scheduled for deletion or archived, so its records are held and nothing in it can be changed.";

    public static bool AdmitsUser(TenantState? state, bool isActiveSystemOwner) =>
        state == TenantState.Active || (state == TenantState.Setup && isActiveSystemOwner);

    public static bool ExchangesMessages(TenantState? state) => state == TenantState.Active;

    public static bool AreLinked(Guid tenantId, Guid? parentOfTenant, Guid otherTenantId, Guid? parentOfOther) =>
        tenantId != otherTenantId && (parentOfTenant == otherTenantId || parentOfOther == tenantId);
}

[TenantWorkflow]
public class Tenant : StateEntityBase<TenantState>
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsRootTenant { get; set; }

    [Display(Name = "Staffing company")]
    public Guid? ParentTenantId { get; set; }
    public ICollection<TenantRole> TenantRoles { get; set; } = [];

    public ICollection<Organization.OrganizationUnit> OrganizationUnits { get; set; } = [];
}
