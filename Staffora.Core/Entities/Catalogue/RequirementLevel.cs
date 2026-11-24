using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Catalogue;

public enum RequirementLevel
{
    [Display(Name = "Required")]
    [Description("A staff member who lacks it is warned about")]
    Required = 0,

    [Display(Name = "Preferred")]
    [Description("Only ranks the candidates, and nobody is warned about it")]
    Preferred = 1,
}

public interface IQualificationRequirement
{
    Guid QualificationId { get; set; }

    RequirementLevel Level { get; set; }
}

public interface IQualificationRequirements<out TRow>
    where TRow : IQualificationRequirement
{
    TRow NewRequirement(Guid qualificationId);
}

public sealed record SlotRequirement(Guid QualificationId, RequirementLevel Level);
