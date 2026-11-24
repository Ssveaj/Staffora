using Staffora.Core.Entities.Organization;
using static Staffora.Services.Demo.DemoRequirement;

namespace Staffora.Services.Demo;

public static class ClearshineDemo
{
    public const string TenantName = "Clearshine Cleaning Services";

    public const string PortalCustomer = "Hagen Bakery";

    private const string Domain = "clearshine.example.com";

    private const string Stockholm = "Stockholm";

    private const string Sodertalje = "Södertälje";

    private const string Norrtalje = "Norrtälje";

    private static readonly DemoPerson senior = DemoPeople.Of("Henrik Dahl", Domain);

    private static readonly DemoPerson[] juniors = [DemoPeople.Of("Leila Farah", Domain), DemoPeople.Of("Connor Byrne", Domain)];

    public static DemoBusiness Business { get; } = new()
    {
        TenantName = TenantName,
        Trade = "cleaning",
        Description = "Sends cleaning crews to offices, shops and food production in Stockholm, Södertälje and Norrtälje.",
        Seed = 8_902,
        Domain = Domain,
        Owners = [DemoPeople.Of("Grace Mwangi", Domain), DemoPeople.Of("Lars Öberg", Domain)],
        SeniorCoordinator = senior,
        JuniorCoordinators = juniors,
        Units =
        [
            new(Stockholm, OrganizationUnitType.Branch, "The depot for the customers in Stockholm.", [senior], PostalArea: "117", Streets: ["Glasbruksgatan", "Tegelviksvägen", "Varvsgränd", "Kopparslagargatan"]),
            new(Sodertalje, OrganizationUnitType.Branch, "The depot for the customers in Södertälje.", [juniors[0]], PostalArea: "151", Streets: ["Slussgatan", "Kanalvägen", "Valsverksgatan", "Brogränd"]),
            new(Norrtalje, OrganizationUnitType.Branch, "The depot for the customers in Norrtälje and along the coast.", [juniors[1]], PostalArea: "761", Streets: ["Hamnbacken", "Roslagsgatan", "Tullportsvägen", "Ankargränd"]),
        ],
        Qualifications = ["Office cleaning", "Window cleaning", "Floor care", "Hygiene training"],
        WorkPatterns =
        [
            new("Early shift", DemoWeek.Weekdays, new TimeOnly(5, 30), new TimeOnly(12, 0)),
            new("Evening shift", DemoWeek.Weekdays, new TimeOnly(15, 30), new TimeOnly(22, 0)),
            new("Mornings, Tuesday to Saturday", [DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday], new TimeOnly(5, 30), new TimeOnly(12, 0)),
        ],
        StaffNames =
        [
            "Ana Petrovic", "Juan Morales", "Fatima Bakr", "Piotr Nowak", "Aisha Bello", "Erik Strand", "Mina Rahimi", "Tobias Weller",
            "Yuki Sato", "Samuel Adeyemi", "Linnea Kask", "Dragan Ilić", "Rosa Medina", "Kofi Asante", "Agnes Wikström", "Bilal Hussein",
            "Tove Lindahl", "Marek Kowalski", "Sara Engel", "Ibrahim Diallo", "Alma Nyström", "Pavel Novák", "Nora Blom", "Ali Karimi",
        ],
        Crews =
        [
            new(Stockholm, "Early shift", ["Office cleaning", "Hygiene training"], 3),
            new(Stockholm, "Early shift", ["Window cleaning", "Floor care"], 2),
            new(Stockholm, "Evening shift", ["Office cleaning", "Floor care"], 3),
            new(Stockholm, "Mornings, Tuesday to Saturday", ["Hygiene training", "Floor care"], 1),
            new(Sodertalje, "Early shift", ["Office cleaning", "Window cleaning"], 2),
            new(Sodertalje, "Evening shift", ["Hygiene training", "Floor care", "Office cleaning"], 3),
            new(Sodertalje, "Mornings, Tuesday to Saturday", ["Window cleaning"], 1),
            new(Norrtalje, "Early shift", ["Office cleaning", "Hygiene training"], 2),
            new(Norrtalje, "Evening shift", ["Office cleaning", "Window cleaning", "Floor care"], 3),
            new(Norrtalje, "Evening shift", ["Hygiene training", "Floor care"], 2),
            new(Norrtalje, "Mornings, Tuesday to Saturday", ["Window cleaning", "Floor care"], 2),
        ],
        Services =
        [
            new("Office cleaning", "Desks, floors, kitchens and washrooms of an office.", 2, true, [Required("Office cleaning"), Preferred("Hygiene training")]),
            new("Deep cleaning", "Thorough cleaning of kitchens and production areas, with floors machine-scrubbed.", 3, true, [Required("Floor care"), Required("Hygiene training")]),
            new("Window cleaning", "Windows, frames and glass inside and out, at height where needed.", 2, true, [Required("Window cleaning")]),
            new("Floor polishing", "Stripping, polishing and sealing of hard floors.", 1, false, [Required("Floor care")]),
            new("Cleaning supervisor", "Leads the crew on site, checks the work and signs the customer's log.", 1, false, [Required("Hygiene training"), Preferred("Office cleaning")]),
        ],
        Customers =
        [
            new(PortalCustomer, Stockholm, ["production kitchen", "shop"]),
            new("Ling & Co Accounting", Stockholm, ["offices", "archive"]),
            new("Brightwater Dental", Stockholm, ["main"]),
            new("Södertälje Logistics Park", Sodertalje, ["warehouse", "offices", "canteen"]),
            new("Kanalen Shopping Centre", Sodertalje, ["main"]),
            new("Roslagen Harbour Hotel", Norrtalje, ["hotel", "conference wing"]),
            new("Norrtälje Sports Hall", Norrtalje, ["main"]),
            new("Grebe Engineering", Sodertalje, ["main"]),
        ],
        AccessNotes =
        [
            "Use the yard door. The night staff let the crew in.",
            "Keys from the manager on duty. The alarm panel is behind the counter.",
            "Collect a key card at the security desk and sign the cleaning log.",
            "Security opens the cage in the basement for the cleaning trolleys.",
            "The loading bay is open from five. Hairnets and shoe covers are by the door.",
        ],
        Portal = new(
            PortalCustomer,
            DemoPeople.Of("Lotte Hagen", "hagenbakery.example.com"),
            DemoPeople.Of("Marco Bianchi", "hagenbakery.example.com"),
            new("Deep clean before the inspection", "Production kitchen, ovens and floors before the food safety inspection.", 1, new TimeOnly(18, 0), new TimeOnly(22, 0),
                [new("Deep cleaning", 3)],
                "Please leave the proving cabinets switched on overnight.",
                "Noted, the crew leaves them on. All three cleaners are confirmed."),
            new("Shop windows and front", "The shop windows inside and out, the door glass and the sign.", 5, new TimeOnly(6, 0), new TimeOnly(9, 0),
                [new("Window cleaning", 2), new("Office cleaning", 1)],
                "Could the crew start before the shop opens at seven?",
                "Yes, the crew starts at six. One window cleaner is confirmed and we are asking for the second.")),
        JobKinds =
        [
            new("Office cleaning", "Office cleaning – ", ["2nd floor", "3rd floor", "4th floor", "reception", "meeting rooms", "open-plan area", "kitchenette", "break room", "print room"], "Desks, floors, kitchens and washrooms.", MaxExtraPeople: 1),
            new("Deep cleaning", "Deep cleaning – ", ["production kitchen", "staff kitchen", "cold room", "washrooms", "loading bay", "packing room", "canteen"], "Machine-scrubbed floors, degreased surfaces and cleaned equipment.", "Cleaning supervisor"),
            new("Window cleaning", "Window cleaning – ", ["street front", "atrium", "shop windows", "skylights", "stairwell", "glass partitions", "entrance canopy"], "Windows and frames inside and out.", MaxExtraPeople: 1),
            new("Floor polishing", "Floor polishing – ", ["lobby", "corridors", "sports floor", "showroom", "dining room"], "Hard floors stripped, polished and sealed."),
        ],
        Shifts =
        [
            new(new TimeOnly(5, 30), new TimeOnly(8, 0)),
            new(new TimeOnly(6, 0), new TimeOnly(8, 30)),
            new(new TimeOnly(6, 30), new TimeOnly(9, 30)),
            new(new TimeOnly(7, 0), new TimeOnly(10, 0)),
            new(new TimeOnly(16, 30), new TimeOnly(19, 0)),
            new(new TimeOnly(17, 0), new TimeOnly(20, 0)),
            new(new TimeOnly(18, 0), new TimeOnly(21, 0)),
            new(new TimeOnly(18, 30), new TimeOnly(21, 30)),
        ],
        WorkingDays = [.. DemoWeek.Weekdays, DayOfWeek.Saturday],
        RecurringPercent = 50,
        LongestRecurringWeeks = 8,
        Texts = new(
            "We could not crew it on that day, so the customer was offered another date.",
            "The customer canceled: the office is closed for a staff day.",
            [
                new(false, ["The alarm code at the offices changed this week. It is in the access notes.", "Got it, thanks."]),
                new(true, ["We are out of floor cleaner on site. Should I bring some from the depot?", "Yes, please take two cans from the depot."]),
                new(true, ["Could I swap Friday's shift with someone on the early shift? I have a family visit."]),
            ],
            ["Is the scrubber machine already on site, or do I take one from the depot?", "Does the customer want the crew to empty the recycling too?"]),
        ClosedDays =
        [
            new("Depot inventory day", 18, 18, Sodertalje),
            new("Staff day", 32, 32),
        ],
    };
}
