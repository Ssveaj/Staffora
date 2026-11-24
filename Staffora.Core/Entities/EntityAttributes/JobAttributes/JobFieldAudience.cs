using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.EntityAttributes.JobAttributes;

public enum JobFieldAudience
{
    [Display(Name = "Coordinators only")]
    [Description("Read by those who read the job: its coordinators and everyone who may view jobs")]
    Coordinators = 0,

    [Display(Name = "Also the assigned staff member")]
    [Description("Read by those who read the job, and by each staff member assigned to one of the job's slots in their own assignment")]
    AssignedStaff = 1,
}

public static class JobFieldAudiences
{
    public static string Label(JobFieldAudience audience) => audience == JobFieldAudience.AssignedStaff ? "Also the assigned staff member" : "Coordinators only";
}
