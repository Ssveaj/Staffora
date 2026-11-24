using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Comments;

public enum CommentAudience
{
    [Display(Name = "Internal")]
    [Description("Notes for the coordinators only")]
    [Icon("lock")]
    Internal = 0,

    [Display(Name = "Staff")]
    [Description("Between the staff member and the coordinators")]
    [Icon("badge")]
    Staff = 1,

    [Display(Name = "Customer")]
    [Description("Between the customer and the coordinators")]
    [Icon("storefront")]
    Customer = 2,
}
