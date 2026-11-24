using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.EntityAttributes;

public enum AttributeValueType
{
    [Display(Name = "Whole number")]
    Integer = 0,

    [Display(Name = "Number")]
    Number = 1,

    [Display(Name = "Date")]
    Date = 2,

    [Display(Name = "Text")]
    Text = 3,

    [Display(Name = "Yes or no")]
    Boolean = 4,

    [Display(Name = "Choice")]
    Choice = 6,
}
