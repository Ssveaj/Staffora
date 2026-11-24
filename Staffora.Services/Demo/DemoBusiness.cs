using System.Globalization;
using System.Text;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Organization;

namespace Staffora.Services.Demo;

public sealed class DemoBusiness
{
    private IReadOnlyList<DemoStaffMember>? staffMembers;
    private Dictionary<string, IReadOnlyList<DemoPlace>>? places;

    public required string TenantName { get; init; }

    public required string Trade { get; init; }

    public required string Description { get; init; }

    public required int Seed { get; init; }

    public required string Domain { get; init; }

    public required IReadOnlyList<DemoPerson> Owners { get; init; }

    public required DemoPerson SeniorCoordinator { get; init; }

    public required IReadOnlyList<DemoPerson> JuniorCoordinators { get; init; }

    public required IReadOnlyList<DemoUnit> Units { get; init; }

    public required IReadOnlyList<string> Qualifications { get; init; }

    public required IReadOnlyList<DemoWorkPattern> WorkPatterns { get; init; }

    public required IReadOnlyList<string> StaffNames { get; init; }

    public required IReadOnlyList<DemoCrew> Crews { get; init; }

    public required IReadOnlyList<DemoCatalogueItem> Services { get; init; }

    public required IReadOnlyList<DemoCustomer> Customers { get; init; }

    public required IReadOnlyList<string> AccessNotes { get; init; }

    public required DemoPortal Portal { get; init; }

    public required IReadOnlyList<DemoJobKind> JobKinds { get; init; }

    public required IReadOnlyList<DemoShift> Shifts { get; init; }

    public required IReadOnlyList<DayOfWeek> WorkingDays { get; init; }

    public required int RecurringPercent { get; init; }

    public required int LongestRecurringWeeks { get; init; }

    public required DemoTexts Texts { get; init; }

    public required IReadOnlyList<DemoClosedDay> ClosedDays { get; init; }

    public bool ClosedOnPublicHolidays { get; init; }

    public IReadOnlyList<DemoPerson> Coordinators => [this.SeniorCoordinator, .. this.JuniorCoordinators];

    public IReadOnlyList<DemoStaffMember> StaffMembers => this.staffMembers ??= this.MakeStaff();

    public IReadOnlyList<DemoPerson> People =>
        [.. this.Owners, .. this.Coordinators, .. this.StaffMembers.Select(staff => staff.Person), this.Portal.CustomerAdministrator, this.Portal.Orderer];

    public DemoPerson Person(string name) => DemoPeople.Of(name, this.Domain);

    public IReadOnlyList<DemoPlace> PlacesOf(DemoCustomer customer) => (this.places ??= this.MakePlaces())[customer.Name];

    public bool IsWithin(string? unit, string ancestor)
    {
        for (var found = this.Units.FirstOrDefault(candidate => candidate.Name == unit); found is not null; found = this.Units.FirstOrDefault(candidate => candidate.Name == found.Parent))
        {
            if (found.Name == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    public string? UnitOf(DemoPerson person) =>
        this.Units.FirstOrDefault(unit => unit.Coordinators.Contains(person))?.Name
        ?? this.StaffMembers.FirstOrDefault(staff => staff.Person == person)?.Unit;

    public string? CityOf(string? unit)
    {
        var found = this.Units.FirstOrDefault(candidate => candidate.Name == unit);
        while (found?.Parent is { } parent)
        {
            found = this.Units.Single(candidate => candidate.Name == parent);
        }

        return found?.Name;
    }

    private List<DemoStaffMember> MakeStaff()
    {
        var names = new Queue<string>(this.StaffNames);
        var staff = new List<DemoStaffMember>();
        foreach (var crew in this.Crews)
        {
            for (var index = 0; index < crew.Count; index++)
            {
                staff.Add(new DemoStaffMember(this.Person(names.Dequeue()), crew.Pattern, crew.Qualifications, crew.Unit));
            }
        }

        return staff;
    }

    private Dictionary<string, IReadOnlyList<DemoPlace>> MakePlaces()
    {
        var random = new Random(this.Seed);
        var made = new Dictionary<string, IReadOnlyList<DemoPlace>>(StringComparer.Ordinal);
        foreach (var customer in this.Customers)
        {
            var city = this.Units.Single(unit => unit.Name == this.CityOf(customer.Unit));
            var streets = city.Streets ?? throw new InvalidOperationException($"{city.Name} has no streets for the places of {customer.Name}.");
            made[customer.Name] = [.. customer.Sites.Select(site => new DemoPlace(
                customer.Sites.Count == 1 ? customer.Name : $"{customer.Name}, {site}",
                $"{streets[random.Next(streets.Count)]} {random.Next(1, 120).ToString(CultureInfo.InvariantCulture)}",
                $"{city.PostalArea} {random.Next(10, 99).ToString(CultureInfo.InvariantCulture)}",
                city.Name,
                this.AccessNotes[random.Next(this.AccessNotes.Count)]))];
        }

        return made;
    }
}

public static class DemoPeople
{
    public static DemoPerson Of(string name, string domain)
    {
        var space = name.IndexOf(' ', StringComparison.Ordinal);
        var (first, last) = (name[..space], name[(space + 1)..]);
        return new DemoPerson(first, last, $"{Plain(first)}.{Plain(last)}@{domain}");
    }

    private static string Plain(string name) =>
        string.Concat(name.Normalize(NormalizationForm.FormD).Where(letter => CharUnicodeInfo.GetUnicodeCategory(letter) != UnicodeCategory.NonSpacingMark && letter != ' ')).ToLowerInvariant();
}

public sealed record DemoWorkPattern(string Name, IReadOnlyList<DayOfWeek> Days, TimeOnly Start, TimeOnly End)
{
    public bool Overnight => this.End <= this.Start;
}

public sealed record DemoCrew(string Unit, string Pattern, IReadOnlyList<string> Qualifications, int Count);

public sealed record DemoStaffMember(DemoPerson Person, string Pattern, IReadOnlyList<string> Qualifications, string Unit);

public sealed record DemoUnit(string Name, OrganizationUnitType Type, string Description, IReadOnlyList<DemoPerson> Coordinators, string? Parent = null, string? PostalArea = null, IReadOnlyList<string>? Streets = null);

public sealed record DemoRequirement(string Qualification, RequirementLevel Level)
{
    public static DemoRequirement Required(string qualification) => new(qualification, RequirementLevel.Required);

    public static DemoRequirement Preferred(string qualification) => new(qualification, RequirementLevel.Preferred);
}

public sealed record DemoCatalogueItem(string Name, string Description, int DefaultCount, bool OfferedInPortal, IReadOnlyList<DemoRequirement> Requirements);

public sealed record DemoPlace(string Name, string StreetAddress, string PostalCode, string City, string AccessNotes);

public sealed record DemoCustomer(string Name, string Unit, IReadOnlyList<string> Sites);

public sealed record DemoOrderLine(string Service, int Count);

public sealed record DemoOrder(string Name, string Description, int DaysAhead, TimeOnly Start, TimeOnly End, IReadOnlyList<DemoOrderLine> Lines, string Question, string Answer);

public sealed record DemoPortal(string Customer, DemoPerson CustomerAdministrator, DemoPerson Orderer, DemoOrder ConfirmedOrder, DemoOrder StaffingOrder);

public sealed record DemoJobKind(string Service, string NamePrefix, IReadOnlyList<string> Variants, string Description, string? ExtraService = null, int MaxExtraPeople = 0);

public sealed record DemoShift(TimeOnly Start, TimeOnly End)
{
    public bool Overnight => this.End <= this.Start;
}

public sealed record DemoClosedDay(string Name, int FirstDayFromMonday, int LastDayFromMonday, string? Unit = null);

public sealed record DemoTexts(string CanceledByUs, string CanceledByCustomer, IReadOnlyList<DemoTalk> Talks, IReadOnlyList<string> Questions);

public sealed record DemoTalk(bool StaffStarts, IReadOnlyList<string> Lines);

public static class DemoWeek
{
    public static IReadOnlyList<DayOfWeek> Weekdays { get; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public static IReadOnlyList<DayOfWeek> EveryDay { get; } = [.. Weekdays, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static IReadOnlyList<DayOfWeek> Weekends { get; } = [DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
}
