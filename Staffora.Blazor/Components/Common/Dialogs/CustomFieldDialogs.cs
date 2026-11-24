using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Services.CustomFields;

namespace Staffora.Blazor.Components.Common.Dialogs;

public sealed class UserFieldDialog : CustomFieldDialog<ApplicationUserAttributeDefinition, UserFieldDialogContext>, IStateEntityDialog<UserFieldDialogContext>
{
    internal static UserFieldDialogContext ContextFor(CustomFieldService service, Guid? fieldId) =>
        new(service, fieldId) { DefaultTitle = "Custom field of users", CreateTitle = "New custom field of users", EntityIcon = "tune" };

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, CustomFieldService service, Guid id, StateEntityDialogOptions? options = null) =>
        dialogService.OpenStateEntityDialogAsync<UserFieldDialog, UserFieldDialogContext>(ContextFor(service, id), options);

    public static Task<StateEntityDialogResult> OpenCreateAsync(DialogService dialogService, CustomFieldService service) =>
        dialogService.OpenStateEntityDialogAsync<UserFieldDialog, UserFieldDialogContext>(ContextFor(service, null));
}

public sealed class UnitFieldDialog : CustomFieldDialog<OrganizationUnitAttributeDefinition, UnitFieldDialogContext>, IStateEntityDialog<UnitFieldDialogContext>
{
    internal static UnitFieldDialogContext ContextFor(CustomFieldService service, Guid? fieldId) =>
        new(service, fieldId) { DefaultTitle = "Custom field of organization units", CreateTitle = "New custom field of organization units", EntityIcon = "tune" };

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, CustomFieldService service, Guid id, StateEntityDialogOptions? options = null) =>
        dialogService.OpenStateEntityDialogAsync<UnitFieldDialog, UnitFieldDialogContext>(ContextFor(service, id), options);

    public static Task<StateEntityDialogResult> OpenCreateAsync(DialogService dialogService, CustomFieldService service) =>
        dialogService.OpenStateEntityDialogAsync<UnitFieldDialog, UnitFieldDialogContext>(ContextFor(service, null));
}

public sealed class JobFieldDialog : CustomFieldDialog<JobAttributeDefinition, JobFieldDialogContext>, IStateEntityDialog<JobFieldDialogContext>
{
    internal static JobFieldDialogContext ContextFor(CustomFieldService service, Guid? fieldId) =>
        new(service, fieldId) { DefaultTitle = "Custom field of jobs", CreateTitle = "New custom field of jobs", EntityIcon = "tune" };

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, CustomFieldService service, Guid id, StateEntityDialogOptions? options = null) =>
        dialogService.OpenStateEntityDialogAsync<JobFieldDialog, JobFieldDialogContext>(ContextFor(service, id), options);

    public static Task<StateEntityDialogResult> OpenCreateAsync(DialogService dialogService, CustomFieldService service) =>
        dialogService.OpenStateEntityDialogAsync<JobFieldDialog, JobFieldDialogContext>(ContextFor(service, null));
}

public sealed record CustomFieldTypeChoice(string Text, AttributeValueType Value);

public sealed record CustomFieldAudienceChoice(string Text, JobFieldAudience Value);
