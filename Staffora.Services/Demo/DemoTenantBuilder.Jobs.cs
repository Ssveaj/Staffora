using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Core.Lists;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;
using static Staffora.Services.Demo.DemoSteps;

namespace Staffora.Services.Demo;

internal sealed partial class DemoTenantBuilder
{
    public const int WaitingOrders = 3;

    public const int PlanTries = 20;

    public static readonly TimeSpan SentEarlierBy = TimeSpan.FromDays(3);

    public static readonly ResponseWindow EarlierWindow = ResponseWindow.OfHours(24);

    public static readonly ResponseWindow CloseWindow = ResponseWindow.OfHours(4);

    private static readonly (Fate Fate, int Count)[] script =
    [
        (Fate.Completed, 20), (Fate.Running, 5), (Fate.FilledRound, 1), (Fate.Staffed, 25), (Fate.StaffedUnconfirmed, 2),
        (Fate.AnsweredRound, 1), (Fate.Yes, 1), (Fate.Question, 2), (Fate.Asked, 4), (Fate.AskedSoon, 2),
        (Fate.OpenRound, 1), (Fate.ExpiredRound, 1), (Fate.ExpiredRequest, 1),
        (Fate.Partly, 14), (Fate.Uncovered, 12), (Fate.Urgent, 4),
        (Fate.CanceledByUs, 2), (Fate.CanceledByCustomer, 2), (Fate.Registered, 2), (Fate.HandedOver, 2),
    ];

    private readonly HashSet<string> jobNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Held> held = [];
    private int questionsAsked;

    private enum Fate
    {
        Completed,
        Running,
        FilledRound,
        Staffed,
        StaffedUnconfirmed,
        AnsweredRound,
        Yes,
        Question,
        Asked,
        AskedSoon,
        OpenRound,
        ExpiredRound,
        ExpiredRequest,
        Partly,
        Uncovered,
        Urgent,
        CanceledByUs,
        CanceledByCustomer,
        Registered,
        HandedOver,
    }

    private async Task JobsAsync(CancellationToken cancellationToken)
    {
        foreach (var (fate, count) in script)
        {
            for (var index = 0; index < count; index++)
            {
                var plan = this.Plan(fate);
                for (var tries = 1; tries < PlanTries && !this.CanStaff(plan, fate); tries++)
                {
                    plan = this.Plan(fate);
                }

                var coordinator = this.CoordinatorFor(plan.Customer);
                await this.BuildJobAsync(await this.SessionAsync(coordinator), coordinator, plan, fate, cancellationToken);
            }
        }
    }

    private async Task BuildJobAsync(IServiceProvider provider, DemoPerson coordinator, PlannedJob plan, Fate fate, CancellationToken cancellationToken)
    {
        var jobs = provider.GetRequiredService<JobService>();
        var staffing = provider.GetRequiredService<SlotAssignmentService>();
        var customer = this.business.Customers.Single(found => found.Name == plan.Customer);
        var place = this.business.PlacesOf(customer)[plan.Place];
        var job = new Job
        {
            Name = plan.Name,
            Description = plan.Kind?.Description,
            CustomerId = this.customerIds[plan.Customer],
            OrganizationUnitId = this.unitIds[customer.Unit],
            Schedule = plan.Recurring ? JobSchedule.Recurring : JobSchedule.Single,
            PeriodStart = plan.Start,
            PeriodEnd = plan.End,
            StartTime = plan.Recurring ? null : plan.Shift.Start,
            EndTime = plan.Recurring ? null : plan.Shift.End,
            PlaceName = place.Name,
            StreetAddress = place.StreetAddress,
            PostalCode = place.PostalCode,
            City = place.City,
            AccessNotes = place.AccessNotes,
            TimeSlots = [.. plan.Days.Select(day => new JobTimeSlot { Weekday = day, Start = plan.Shift.Start, End = plan.Shift.End })],
        };
        var jobId = Expect(await jobs.CreateJobAsync(job, JobWorkflowAttribute.RegisterStep, cancellationToken), $"register {plan.Name}");
        var service = plan.Kind!.Service;
        var main = await AddSlotsAsync(jobs, jobId, this.ServiceIdOf(service), plan.Count, cancellationToken);
        Guid? extra = plan.Extra is { } extraService ? await AddSlotsAsync(jobs, jobId, this.ServiceIdOf(extraService), 1, cancellationToken) : null;

        if (fate == Fate.Registered)
        {
            return;
        }

        if (fate == Fate.HandedOver)
        {
            await StepAsync(jobs, jobId, JobWorkflowAttribute.HandOverStep, null, cancellationToken);
            return;
        }

        await StepAsync(jobs, jobId, JobWorkflowAttribute.StartFromRegistrationStep, null, cancellationToken);
        switch (fate)
        {
            case Fate.FilledRound:
                await this.SendRoundAsync(provider.GetRequiredService<RequestRoundService>(), plan, main, service, plan.Count + 2, firstToAccept: true, answers: plan.Count, null, cancellationToken);
                break;
            case Fate.OpenRound:
                await this.SendRoundAsync(provider.GetRequiredService<RequestRoundService>(), plan, main, service, 4, firstToAccept: true, answers: 0, null, cancellationToken);
                break;
            case Fate.AnsweredRound:
                await this.SendRoundAsync(provider.GetRequiredService<RequestRoundService>(), plan, main, service, 3, firstToAccept: false, answers: 2, null, cancellationToken);
                break;
            case Fate.ExpiredRound:
                await this.SendRoundAsync(this.SentEarlier(provider).Rounds, plan, main, service, 3, firstToAccept: true, answers: 0, EarlierWindow, cancellationToken);
                break;
            case Fate.Asked:
                await this.RequestAsync(staffing, plan, main, service, null, cancellationToken);
                break;
            case Fate.AskedSoon:
                await this.RequestAsync(staffing, plan, main, service, CloseWindow, cancellationToken);
                break;
            case Fate.ExpiredRequest:
                await this.RequestAsync(this.SentEarlier(provider).Staffing, plan, main, service, EarlierWindow, cancellationToken);
                break;
            case Fate.Yes:
                if (await this.RequestAsync(staffing, plan, main, service, null, cancellationToken) is { } yes)
                {
                    await this.AnswerAsync(yes.Resource, yes.Request, SlotAssignmentWorkflowAttribute.AcceptStep, null, cancellationToken);
                    this.bookings.Book(yes.Resource, this.Occurrences(plan));
                }

                break;
            case Fate.Question:
                if (await this.RequestAsync(staffing, plan, main, service, null, cancellationToken) is { } asked)
                {
                    var questions = this.business.Texts.Questions;
                    await this.AnswerAsync(asked.Resource, asked.Request, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, questions[this.questionsAsked++ % questions.Count], cancellationToken);
                }

                break;
            case Fate.Partly:
                await this.AssignAsync(staffing, plan, main, service, 1, coordinator, cancellationToken);
                break;
            case Fate.Running or Fate.Staffed or Fate.StaffedUnconfirmed or Fate.Completed:
                await this.AssignAsync(staffing, plan, main, service, plan.Count, coordinator, cancellationToken);
                break;
            case Fate.CanceledByUs:
                await StepAsync(jobs, jobId, JobWorkflowAttribute.CancelStepFrom(JobState.Coordinating), this.business.Texts.CanceledByUs, cancellationToken);
                return;
            case Fate.CanceledByCustomer:
                await StepAsync(jobs, jobId, JobWorkflowAttribute.CustomerCancelStepFrom(JobState.Coordinating), this.business.Texts.CanceledByCustomer, cancellationToken);
                return;
        }

        if (extra is { } extraSlot && fate is Fate.FilledRound or Fate.Running or Fate.Staffed or Fate.StaffedUnconfirmed or Fate.Completed)
        {
            await this.AssignAsync(staffing, plan, extraSlot, plan.Extra!, 1, coordinator, cancellationToken);
        }

        if (fate is Fate.FilledRound or Fate.Running or Fate.Staffed or Fate.Completed)
        {
            await StepAsync(jobs, jobId, JobWorkflowAttribute.ConfirmStep, null, cancellationToken);
        }
    }

    private bool CanStaff(PlannedJob plan, Fate fate)
    {
        var needed = fate switch
        {
            Fate.Completed or Fate.Running or Fate.Staffed or Fate.StaffedUnconfirmed => plan.Count,
            Fate.FilledRound => plan.Count + 1,
            Fate.AnsweredRound => 2,
            Fate.OpenRound or Fate.ExpiredRound or Fate.Asked or Fate.AskedSoon or Fate.ExpiredRequest or Fate.Yes or Fate.Question or Fate.Partly => 1,
            _ => 0,
        };
        return this.Candidates(plan, plan.Kind!.Service).Take(needed).Count() == needed
            && (needed == 0 || plan.Extra is not { } extra || this.Candidates(plan, extra).Any());
    }

    private (SlotAssignmentService Staffing, RequestRoundService Rounds) SentEarlier(IServiceProvider provider)
    {
        var earlier = new DemoSentEarlier(this.clock, SentEarlierBy);
        var workflows = ActivatorUtilities.CreateInstance<WorkflowService>(provider, earlier);
        return (ActivatorUtilities.CreateInstance<SlotAssignmentService>(provider, earlier, workflows), ActivatorUtilities.CreateInstance<RequestRoundService>(provider, earlier, workflows));
    }

    private async Task SendRoundAsync(RequestRoundService rounds, PlannedJob plan, Guid slotId, string service, int size, bool firstToAccept, int answers, ResponseWindow? within, CancellationToken cancellationToken)
    {
        var asked = this.Candidates(plan, service).Take(size).ToList();
        if (asked.Count == 0)
        {
            return;
        }

        var roundId = Expect(await rounds.SendRoundAsync(slotId, asked, firstToAccept, within, cancellationToken), $"send a round for {plan.Name}");
        if (answers == 0)
        {
            return;
        }

        var requests = Expect(await rounds.QueryRoundRequestsAsync(roundId, new ListQuery { PageSize = ListDefinition.MaxPageSize }, cancellationToken), $"read the round of {plan.Name}").Rows;
        foreach (var resource in asked.Take(answers))
        {
            var request = requests.Single(row => this.resourceByName[row.Staff] == resource).Id;
            await this.AnswerAsync(resource, request, SlotAssignmentWorkflowAttribute.AcceptStep, null, cancellationToken);
            this.bookings.Book(resource, this.Occurrences(plan));
        }
    }

    private async Task AnswerAsync(Guid resource, Guid request, string step, string? comment, CancellationToken cancellationToken)
    {
        var person = this.staffOf[resource].Person;
        var assignments = (await this.SessionAsync(person)).GetRequiredService<SlotAssignmentService>();
        var updater = Expect(await assignments.LoadSlotAssignmentUpdaterAsync(request, cancellationToken), $"open the request of {person.Email}");
        Expect(await assignments.UpdateSlotAssignmentAndTransitionAsync(updater, step, comment, cancellationToken), $"take the step {step} on the request of {person.Email}");
    }

    private async Task AssignAsync(SlotAssignmentService staffing, PlannedJob plan, Guid slotId, string service, int count, DemoPerson coordinator, CancellationToken cancellationToken)
    {
        var assigned = 0;
        foreach (var resource in this.Candidates(plan, service).ToList())
        {
            if (assigned == count)
            {
                return;
            }

            var answer = await staffing.AssignStaffAsync(slotId, resource, cancellationToken);
            if (answer.IsSuccess)
            {
                assigned++;
                this.bookings.Book(resource, this.Occurrences(plan));
                this.held.Add(new Held(answer.Value, resource, coordinator, plan));
            }
        }
    }

    private async Task<(Guid Request, Guid Resource)?> RequestAsync(SlotAssignmentService staffing, PlannedJob plan, Guid slotId, string service, ResponseWindow? within, CancellationToken cancellationToken)
    {
        foreach (var resource in this.Candidates(plan, service).ToList())
        {
            var asked = within is null ? await staffing.RequestStaffAsync(slotId, resource, cancellationToken) : await staffing.RequestStaffAsync(slotId, resource, within, cancellationToken);
            if (asked.IsSuccess)
            {
                return (asked.Value, resource);
            }
        }

        return null;
    }

    private PlannedJob Plan(Fate fate)
    {
        var group = fate is Fate.FilledRound or Fate.OpenRound or Fate.AnsweredRound or Fate.ExpiredRound or Fate.Partly;
        var (kind, name) = this.PickName(group);
        var service = this.ServiceOf(kind.Service);
        var customer = this.business.Customers[this.random.Next(this.business.Customers.Count)];
        var place = this.random.Next(this.business.PlacesOf(customer).Count);
        var count = Math.Max(group ? 2 : 1, service.DefaultCount + this.random.Next(kind.MaxExtraPeople + 1));
        var extra = kind.ExtraService is { } second && this.random.Next(2) == 0 ? second : null;
        var recurring = fate switch
        {
            Fate.Running => true,
            Fate.Completed or Fate.Staffed or Fate.StaffedUnconfirmed or Fate.Partly or Fate.Uncovered or Fate.Registered or Fate.HandedOver => this.random.Next(100) < this.business.RecurringPercent,
            _ => false,
        };
        var shifts = this.business.Shifts.Where(shift => !recurring || !shift.Overnight).ToList();
        var shift = shifts[this.random.Next(shifts.Count)];

        if (!recurring)
        {
            var day = fate switch
            {
                Fate.Completed => this.PickDay(this.firstDay, this.today.AddDays(-1)),
                Fate.Urgent => this.PickDay(this.today.AddDays(1), this.today.AddDays(2)),
                _ => this.PickDay(this.today.AddDays(1), this.lastDay),
            };
            return new PlannedJob(name, kind, customer.Name, false, day, JobRules.SingleLastDay(day, shift.Start, shift.End), [], shift, count, extra, place);
        }

        var (start, end) = fate switch
        {
            Fate.Completed => (this.monday.AddDays(-7 * this.random.Next(2, WeeksBack + 1)), this.monday.AddDays(-1)),
            Fate.Running => (this.monday.AddDays(-7 * this.random.Next(1, WeeksBack)), this.monday.AddDays(7 * this.random.Next(3, WeeksAhead) - 1)),
            _ => this.FuturePeriod(),
        };
        var allowed = WeekdayExtensions.Week.Where(day => this.business.WorkingDays.Contains(day.ToDayOfWeek()) && !this.ClosedOn(day, start, end)).ToList();
        var days = (allowed.Count > 0 ? allowed : [.. WeekdayExtensions.Week.Where(day => this.business.WorkingDays.Contains(day.ToDayOfWeek()))])
            .OrderBy(_ => this.random.Next()).Take(this.random.Next(1, 4)).Order().ToList();
        return new PlannedJob(name, kind, customer.Name, true, start, end, days, shift, count, extra, place);
    }

    private (DateOnly Start, DateOnly End) FuturePeriod()
    {
        var start = this.monday.AddDays(7 * this.random.Next(1, 5));
        var end = start.AddDays(7 * this.random.Next(3, this.business.LongestRecurringWeeks + 1) - 1);
        return (start, end < this.lastDay ? end : this.lastDay);
    }

    private bool ClosedOn(Weekday weekday, DateOnly start, DateOnly end)
    {
        for (var day = start > this.today ? start : this.today; day <= end; day = day.AddDays(1))
        {
            if (WeekdayExtensions.WeekdayOf(day) == weekday && this.closedDates.Contains(day))
            {
                return true;
            }
        }

        return false;
    }

    private DateOnly PickDay(DateOnly first, DateOnly last)
    {
        var open = new List<DateOnly>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (this.IsWorkday(day))
            {
                open.Add(day);
            }
        }

        return open.Count > 0 ? open[this.random.Next(open.Count)] : first;
    }

    private DateOnly DayAhead(int ahead)
    {
        var day = this.today;
        for (var counted = 0; counted <= ahead;)
        {
            day = day.AddDays(1);
            if (this.IsWorkday(day))
            {
                counted++;
            }
        }

        return day;
    }

    private bool IsWorkday(DateOnly day) => this.business.WorkingDays.Contains(day.DayOfWeek) && !this.closedDates.Contains(day);

    private (DemoJobKind Kind, string Name) PickName(bool group)
    {
        var kinds = this.business.JobKinds;
        var preferred = group ? kinds.Where(kind => this.ServiceOf(kind.Service).DefaultCount + kind.MaxExtraPeople >= 2).ToList() : [];
        var pool = (preferred.Count > 0 ? preferred : kinds).SelectMany(kind => kind.Variants.Select(variant => (Kind: kind, Name: kind.NamePrefix + variant))).ToList();
        var open = pool.Where(choice => !this.jobNames.Contains(choice.Name)).ToList();
        var chosen = open.Count > 0 ? open[this.random.Next(open.Count)] : pool[this.random.Next(pool.Count)];
        this.jobNames.Add(chosen.Name);
        return chosen;
    }

    private IEnumerable<Guid> Candidates(PlannedJob plan, string service)
    {
        var required = this.ServiceOf(service).Requirements.Where(requirement => requirement.Level == RequirementLevel.Required).Select(requirement => requirement.Qualification).ToList();
        var occurrences = this.Occurrences(plan);
        var city = this.business.CityOf(this.business.Customers.Single(customer => customer.Name == plan.Customer).Unit);
        return this.staffOf
            .Where(staff => required.All(staff.Value.Qualifications.Contains) && this.bookings.IsFree(staff.Key, occurrences))
            .Select(staff => (Resource: staff.Key, Covered: DemoBookings.Covers(this.business.WorkPatterns.Single(pattern => pattern.Name == staff.Value.Pattern), occurrences), Local: this.business.CityOf(staff.Value.Unit) == city, Order: this.random.Next()))
            .ToList()
            .OrderByDescending(staff => staff.Covered)
            .ThenByDescending(staff => staff.Local)
            .ThenBy(staff => staff.Order)
            .Select(staff => staff.Resource);
    }

    private List<DemoBooking> Occurrences(PlannedJob plan)
    {
        if (!plan.Recurring)
        {
            return [DemoBooking.Of(plan.Start, plan.Shift)];
        }

        var occurrences = new List<DemoBooking>();
        for (var day = plan.Start; day <= plan.End; day = day.AddDays(1))
        {
            if (plan.Days.Contains(WeekdayExtensions.WeekdayOf(day)))
            {
                occurrences.Add(DemoBooking.Of(day, plan.Shift));
            }
        }

        return occurrences;
    }

    private DemoPerson CoordinatorFor(string customer)
    {
        var city = this.business.CityOf(this.business.Customers.Single(found => found.Name == customer).Unit);
        return this.business.Coordinators.Single(coordinator => this.business.CityOf(this.business.UnitOf(coordinator)) == city);
    }

    private async Task TalksAsync(CancellationToken cancellationToken)
    {
        var talks = this.business.Texts.Talks;
        var upcoming = this.held.Where(entry => entry.Plan.End >= this.today).ToList();
        for (var index = 0; index < talks.Count && index * 2 < upcoming.Count; index++)
        {
            var (assignment, resource, coordinator, _) = upcoming[index * 2];
            var staff = this.staffOf[resource].Person;
            var talk = talks[index];
            for (var line = 0; line < talk.Lines.Count; line++)
            {
                var speaker = talk.StaffStarts == (line % 2 == 0) ? staff : coordinator;
                var conversations = (await this.SessionAsync(speaker)).GetRequiredService<StaffConversationService>();
                Expect(await conversations.AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, talk.Lines[line], cancellationToken), $"write on an assignment as {speaker.Email}");
            }
        }
    }

    private static async Task<Guid> AddSlotsAsync(JobService jobs, Guid jobId, Guid serviceId, int count, CancellationToken cancellationToken)
    {
        var slot = Expect(await jobs.Slots.NewJobSlotFromServiceAsync(jobId, serviceId, cancellationToken), "make a slot from a service");
        slot.Count = count;
        return Expect(await jobs.Slots.CreateJobSlotAsync(slot, JobSlotWorkflowAttribute.AddStep, cancellationToken), $"add the slot {slot.Name}");
    }

    private sealed record PlannedJob(string Name, DemoJobKind? Kind, string Customer, bool Recurring, DateOnly Start, DateOnly End, IReadOnlyList<Weekday> Days, DemoShift Shift, int Count, string? Extra, int Place = 0);

    private sealed record Held(Guid Assignment, Guid Resource, DemoPerson Coordinator, PlannedJob Plan);
}
