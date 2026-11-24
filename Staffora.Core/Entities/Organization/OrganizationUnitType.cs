using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Organization;

public enum OrganizationUnitType
{
    [Display(Name = "Company")]
    [Description("The business itself: the root of the tree, one per tenant")]
    [Icon("corporate_fare")]
    Company = 0,

    [Display(Name = "Region")]
    [Description("A geographical part of the company")]
    [Icon("public")]
    Region = 1,

    [Display(Name = "Branch")]
    [Description("An office or site where people work")]
    [Icon("apartment")]
    Branch = 2,

    [Display(Name = "Team")]
    [Description("A team of people within the company")]
    [Icon("groups")]
    Team = 3,

    [Display(Name = "Grouping")]
    [Description("Orders the units below it and holds no roles or members")]
    [Icon("folder")]
    Grouping = 4,
}
