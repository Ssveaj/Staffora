using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Services.Jobs;

public static class JobWorkflowQueries
{
    public static IQueryable<Job> WithWorkflowGraph(this IQueryable<Job> query) => query
        .Include(job => job.Customer)
        .Include(job => job.Contact)
        .Include(job => job.OrganizationUnit)
        .Include(job => job.Coordinator)
        .Include(job => job.TimeSlots)
        .Include(job => job.Attributes)
        .Include(job => job.Slots).ThenInclude(slot => slot.Assignments).ThenInclude(assignment => assignment.Resource).ThenInclude(resource => resource.User)
        .AsSplitQuery();
}

public static class JobSlotWorkflowQueries
{
    public static IQueryable<JobSlot> WithWorkflowGraph(this IQueryable<JobSlot> query) => query
        .Include(slot => slot.Job)
        .Include(slot => slot.Service)
        .Include(slot => slot.Qualifications).ThenInclude(required => required.Qualification)
        .Include(slot => slot.Assignments).ThenInclude(assignment => assignment.Resource).ThenInclude(resource => resource.User)
        .AsSplitQuery();
}

public static class SlotAssignmentWorkflowQueries
{
    public static IQueryable<SlotAssignment> WithWorkflowGraph(this IQueryable<SlotAssignment> query) => query
        .Include(assignment => assignment.Slot).ThenInclude(slot => slot.Job).ThenInclude(job => job.TimeSlots)
        .Include(assignment => assignment.Slot).ThenInclude(slot => slot.Job).ThenInclude(job => job.Customer)
        .Include(assignment => assignment.Slot).ThenInclude(slot => slot.Job).ThenInclude(job => job.Coordinator)
        .Include(assignment => assignment.Slot).ThenInclude(slot => slot.Assignments).ThenInclude(other => other.Resource).ThenInclude(resource => resource.User)
        .Include(assignment => assignment.Slot).ThenInclude(slot => slot.Job).ThenInclude(job => job.Slots).ThenInclude(slot => slot.Qualifications)
        .Include(assignment => assignment.Slot).ThenInclude(slot => slot.Job).ThenInclude(job => job.Slots).ThenInclude(slot => slot.Assignments).ThenInclude(other => other.Resource).ThenInclude(resource => resource.User)
        .Include(assignment => assignment.Resource).ThenInclude(resource => resource.User)
        .Include(assignment => assignment.Round)
        .AsSplitQuery();
}

public static class RequestRoundWorkflowQueries
{
    public static IQueryable<RequestRound> WithWorkflowGraph(this IQueryable<RequestRound> query) => query
        .Include(round => round.Slot).ThenInclude(slot => slot.Job).ThenInclude(job => job.Slots).ThenInclude(slot => slot.Qualifications)
        .Include(round => round.Slot).ThenInclude(slot => slot.Job).ThenInclude(job => job.Slots).ThenInclude(slot => slot.Assignments).ThenInclude(assignment => assignment.Resource).ThenInclude(resource => resource.User)
        .Include(round => round.Requests).ThenInclude(request => request.Slot).ThenInclude(slot => slot.Job)
        .Include(round => round.Requests).ThenInclude(request => request.Resource).ThenInclude(resource => resource.User)
        .AsSplitQuery();
}
