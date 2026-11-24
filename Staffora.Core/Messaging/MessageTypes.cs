namespace Staffora.Core.Messaging;

public static class MessageTypes
{
    public static readonly MessageType UserReactivated = new("user-reactivated", MessageCategory.Account, "Account reactivated", "FirstName", "LastName");

    public static readonly MessageType AbsenceLeaveRequested = new("absence-leave-requested", MessageCategory.Absence, "Leave requested", "Staff", "Period", "AbsenceId");

    public static readonly MessageType AbsenceSickReported = new("absence-sick-reported", MessageCategory.Absence, "Sick leave reported", "Staff", "Period", "AbsenceId");

    public static readonly MessageType AbsenceRegistered = new("absence-registered", MessageCategory.Absence, "Absence registered for you", "FirstName", "Kind", "Period", "Status", "AbsenceId");

    public static readonly MessageType AbsenceApproved = new("absence-approved", MessageCategory.Absence, "Absence approved", "FirstName", "Kind", "Period", "AbsenceId");

    public static readonly MessageType AbsenceRejected = new("absence-rejected", MessageCategory.Absence, "Absence rejected", "FirstName", "Kind", "Period", "AbsenceId");

    public static readonly MessageType AbsenceCanceled = new("absence-canceled", MessageCategory.Absence, "Absence canceled", "FirstName", "Kind", "Period", "AbsenceId");

    public static readonly MessageType AbsenceQuestionToStaff = new("absence-question-to-staff", MessageCategory.Absence, "Question about your absence", "FirstName", "Kind", "Period", "AbsenceId");

    public static readonly MessageType AbsenceAnswerToStaff = new("absence-answer-to-staff", MessageCategory.Absence, "Answer about your absence", "FirstName", "Kind", "Period", "AbsenceId");

    public static readonly MessageType AbsenceQuestionFromStaff = new("absence-question-from-staff", MessageCategory.Absence, "Question about an absence", "Staff", "Kind", "Period", "AbsenceId");

    public static readonly MessageType AbsenceAnswerFromStaff = new("absence-answer-from-staff", MessageCategory.Absence, "Answer on an absence", "Staff", "Kind", "Period", "AbsenceId");

    public static readonly MessageType AbsenceCanceledByStaff = new("absence-canceled-by-staff", MessageCategory.Absence, "Absence canceled by the staff member", "Staff", "Kind", "Period", "AbsenceId");

    public static readonly MessageType AssignmentRequested = new("assignment-requested", MessageCategory.Staffing, "Request to work", "FirstName", "Job", "Slot", "Period", "AssignmentId");

    public static readonly MessageType AssignmentAccepted = new("assignment-accepted", MessageCategory.Staffing, "Request accepted", "Staff", "Job", "Slot", "AssignmentId");

    public static readonly MessageType AssignmentDeclined = new("assignment-declined", MessageCategory.Staffing, "Request declined", "Staff", "Job", "Slot", "AssignmentId");

    public static readonly MessageType AssignmentWithdrawn = new("assignment-withdrawn", MessageCategory.Staffing, "Yes taken back", "Staff", "Job", "Slot", "AssignmentId");

    public static readonly MessageType AssignmentAssigned = new("assignment-assigned", MessageCategory.Staffing, "Assigned to work", "FirstName", "Job", "Slot", "Period", "AssignmentId");

    public static readonly MessageType AssignmentRequestedInRound = new("assignment-requested-in-round", MessageCategory.Staffing, "Request in a round", "FirstName", "Job", "Slot", "Period", "Rule", "AssignmentId");

    public static readonly MessageType AssignmentTaken = new("assignment-taken", MessageCategory.Staffing, "Slot taken", "Staff", "Job", "Slot", "AssignmentId");

    public static readonly MessageType RoundFilled = new("round-filled", MessageCategory.Staffing, "Request round filled", "Job", "Slot", "Names", "RoundId");

    public static readonly MessageType AssignmentExpired = new("assignment-expired", MessageCategory.Staffing, "Request expired", "Staff", "Job", "Slot", "AssignmentId");

    public static readonly MessageType RoundExpired = new("round-expired", MessageCategory.Staffing, "Request round expired", "Job", "Slot", "Names", "RoundId");

    public static readonly MessageType AssignmentNotSelected = new("assignment-not-selected", MessageCategory.Staffing, "Not selected", "FirstName", "Job", "Slot", "AssignmentId");

    public static readonly MessageType AssignmentCanceled = new("assignment-canceled", MessageCategory.Staffing, "Assignment canceled", "FirstName", "Job", "Slot", "AssignmentId");

    public static readonly MessageType AssignmentTakenOff = new("assignment-taken-off", MessageCategory.Staffing, "Taken off for absence", "FirstName", "Job", "Slot", "Absence", "AssignmentId");

    public static readonly MessageType AssignmentQuestionFromStaff = new("assignment-question-from-staff", MessageCategory.Staffing, "Question about a request", "Staff", "Job", "Slot", "AssignmentId");

    public static readonly MessageType AssignmentAnswerToStaff = new("assignment-answer-to-staff", MessageCategory.Staffing, "Answer about your request", "FirstName", "Job", "Slot", "AssignmentId");

    public static readonly MessageType AssignmentStaffWrote = new("assignment-staff-wrote", MessageCategory.Conversation, "Staff member wrote", "Staff", "Job", "Slot", "AssignmentId");

    public static readonly MessageType AssignmentCoordinatorWrote = new("assignment-coordinator-wrote", MessageCategory.Conversation, "Coordinator wrote", "FirstName", "Author", "Job", "Slot", "AssignmentId");

    public static readonly MessageType JobRequested = new("job-requested", MessageCategory.Ordering, "Job ordered", "Customer", "Job", "Period", "JobId");

    public static readonly MessageType OrderConfirmed = new("order-confirmed", MessageCategory.Ordering, "Order confirmed", "Job", "Period", "OrderId");

    public static readonly MessageType OrderCanceledBySupplier = new("order-canceled-by-supplier", MessageCategory.Ordering, "Order canceled by the staffing company", "Job", "Period", "OrderId");

    public static readonly MessageType OrderDeclined = new("order-declined", MessageCategory.Ordering, "Order declined", "Job", "Period", "OrderId");

    public static readonly MessageType OrderCompleted = new("order-completed", MessageCategory.Ordering, "Job completed", "Job", "Period", "OrderId");

    public static readonly MessageType JobCanceledInPortal = new("job-canceled-in-portal", MessageCategory.Ordering, "Job canceled by the customer", "Customer", "Job", "Period", "JobId");

    public static readonly MessageType JobCustomerWrote = new("job-customer-wrote", MessageCategory.Conversation, "Customer wrote", "Customer", "Author", "Job", "JobId");

    public static readonly MessageType OrderSupplierWrote = new("order-supplier-wrote", MessageCategory.Conversation, "Staffing company wrote", "Supplier", "Job", "OrderId");

    public static IReadOnlyList<MessageType> All { get; } =
    [
        UserReactivated, AbsenceLeaveRequested, AbsenceSickReported, AbsenceRegistered, AbsenceApproved, AbsenceRejected, AbsenceCanceled,
        AbsenceQuestionToStaff, AbsenceAnswerToStaff, AbsenceQuestionFromStaff, AbsenceAnswerFromStaff, AbsenceCanceledByStaff,
        AssignmentRequested, AssignmentAccepted, AssignmentDeclined, AssignmentWithdrawn, AssignmentAssigned, AssignmentNotSelected, AssignmentCanceled, AssignmentTakenOff,
        AssignmentRequestedInRound, AssignmentTaken, RoundFilled, AssignmentExpired, RoundExpired, AssignmentQuestionFromStaff, AssignmentAnswerToStaff,
        AssignmentStaffWrote, AssignmentCoordinatorWrote, JobRequested,
        OrderConfirmed, OrderCanceledBySupplier, OrderDeclined, OrderCompleted, JobCanceledInPortal,
        JobCustomerWrote, OrderSupplierWrote,
    ];
}
