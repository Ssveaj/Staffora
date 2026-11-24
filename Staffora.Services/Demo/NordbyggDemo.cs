using Staffora.Core.Entities.Organization;
using static Staffora.Services.Demo.DemoRequirement;

namespace Staffora.Services.Demo;

public static class NordbyggDemo
{
    public const string TenantName = "Nordbygg Construction";

    public const string PortalCustomer = "Kvarnby Housing";

    private const string Domain = "nordbygg.example.com";

    private const string Malmo = "Malmö";

    private const string Lund = "Lund";

    private const string Helsingborg = "Helsingborg";

    private static readonly DemoPerson senior = DemoPeople.Of("Mikael Sjöberg", Domain);

    private static readonly DemoPerson[] juniors = [DemoPeople.Of("Ayşe Yilmaz", Domain), DemoPeople.Of("Patrik Holmén", Domain)];

    public static DemoBusiness Business { get; } = new()
    {
        TenantName = TenantName,
        Trade = "construction",
        Description = "Hires out carpenters, electricians, scaffolders and site crews to building sites in Malmö, Lund and Helsingborg.",
        Seed = 8_903,
        Domain = Domain,
        Owners = [DemoPeople.Of("Karin Nordqvist", Domain), DemoPeople.Of("Fredrik Malm", Domain)],
        SeniorCoordinator = senior,
        JuniorCoordinators = juniors,
        Units =
        [
            new(Malmo, OrganizationUnitType.Branch, "The office for the sites in Malmö.", [senior], PostalArea: "211", Streets: ["Varvskajen", "Tegelbruksvägen", "Smedjegatan", "Kranbanan"]),
            new(Lund, OrganizationUnitType.Branch, "The office for the sites in and around Lund.", [juniors[0]], PostalArea: "223", Streets: ["Kvarngatan", "Murarvägen", "Stenhuggaregränd", "Gjutarvägen"]),
            new(Helsingborg, OrganizationUnitType.Branch, "The office for the sites in Helsingborg.", [juniors[1]], PostalArea: "252", Streets: ["Hamnplanen", "Timmervägen", "Spårgatan", "Brokajen"]),
        ],
        Qualifications = ["Carpenter", "Electrician authorization", "Scaffolding", "Forklift licence", "ID06 card"],
        WorkPatterns =
        [
            new("Day shift", DemoWeek.Weekdays, new TimeOnly(7, 0), new TimeOnly(16, 0)),
            new("Early day shift", DemoWeek.Weekdays, new TimeOnly(6, 0), new TimeOnly(15, 0)),
        ],
        StaffNames =
        [
            "Björn Lindqvist", "Dariusz Wójcik", "Emma Rosén", "Goran Petrović", "Hampus Ek", "Ismail Osman", "Jonas Strömberg", "Kalle Hedlund",
            "Lina Forsberg", "Magnus Berglund", "Nils Ahlgren", "Oskar Lundin", "Per Sundqvist", "Rasmus Kjellberg", "Sven Håkansson", "Teodor Wiklund",
            "Ulrik Dahlin", "Vera Lindström", "Wilhelm Brandt", "Zoran Marković", "Adam Nordin", "Beatrice Falk", "Calle Svensson", "David Engström",
        ],
        Crews =
        [
            new(Malmo, "Day shift", ["Carpenter", "ID06 card"], 3),
            new(Malmo, "Day shift", ["Electrician authorization", "ID06 card"], 2),
            new(Malmo, "Early day shift", ["Scaffolding", "ID06 card"], 2),
            new(Malmo, "Day shift", ["Forklift licence", "ID06 card"], 1),
            new(Lund, "Day shift", ["Carpenter", "ID06 card"], 2),
            new(Lund, "Day shift", ["Electrician authorization", "ID06 card"], 2),
            new(Lund, "Early day shift", ["Scaffolding", "Forklift licence", "ID06 card"], 2),
            new(Lund, "Day shift", ["ID06 card"], 2),
            new(Helsingborg, "Day shift", ["Carpenter", "Scaffolding", "ID06 card"], 3),
            new(Helsingborg, "Day shift", ["Electrician authorization", "ID06 card"], 1),
            new(Helsingborg, "Early day shift", ["Forklift licence", "ID06 card"], 2),
            new(Helsingborg, "Day shift", ["Carpenter", "ID06 card"], 2),
        ],
        Services =
        [
            new("Carpentry", "Framing, formwork and finishing on site, following the site manager's plan.", 2, true, [Required("Carpenter"), Required("ID06 card")]),
            new("Electrical installation", "Wiring, panels and fittings by an authorized electrician.", 1, true, [Required("Electrician authorization"), Required("ID06 card")]),
            new("Scaffolding crew", "Puts up, moves and takes down scaffolding.", 3, true, [Required("Scaffolding"), Required("ID06 card")]),
            new("Forklift driver", "Moves materials around the site and unloads deliveries.", 1, false, [Required("Forklift licence"), Required("ID06 card")]),
            new("Site labourer", "Clearing, carrying and helping the trades on site.", 2, true, [Required("ID06 card"), Preferred("Forklift licence")]),
        ],
        Customers =
        [
            new(PortalCustomer, Malmo, ["block A", "block B", "site office"]),
            new("Öresund Property Group", Malmo, ["harbour site"]),
            new("Lundaslätten Builders", Lund, ["north site", "south site"]),
            new("Campus Nord Developments", Lund, ["main"]),
            new("Helsing Port Logistics", Helsingborg, ["terminal 2", "warehouse"]),
            new("Sundvik Homes", Helsingborg, ["main"]),
            new("Skåne Rail Works", Lund, ["depot"]),
            new("Ribban Retail Park", Malmo, ["main"]),
        ],
        AccessNotes =
        [
            "Sign in at the site office and show your ID06 card at the gate.",
            "Hard hat, boots and a high-visibility vest are required beyond the fence.",
            "Park outside the gate. The site manager meets new staff at seven.",
            "Deliveries come through gate 3. The forklift keys are in the site office.",
            "The canteen hut is by the crane. Breaks are at nine and twelve.",
        ],
        Portal = new(
            PortalCustomer,
            DemoPeople.Of("Ingela Kvarnström", "kvarnby.example.com"),
            DemoPeople.Of("Tarik Hasanović", "kvarnby.example.com"),
            new("Scaffolding for the facade, block A", "Scaffolding up along the south facade before the window fitters start.", 3, new TimeOnly(7, 0), new TimeOnly(16, 0),
                [new("Scaffolding crew", 3)],
                "Can the crew use the tower crane in the morning?",
                "Yes, the crane is booked for them until ten. The whole crew is confirmed."),
            new("Stairwell fit-out, block B", "Handrails, doors and lighting in two stairwells.", 7, new TimeOnly(7, 0), new TimeOnly(16, 0),
                [new("Carpentry", 2), new("Electrical installation", 1)],
                "The lifts are out of use that week. Is that a problem for the crew?",
                "No problem, they carry by hand. The electrician is confirmed and we are finding the carpenters.")),
        JobKinds =
        [
            new("Carpentry", "Carpentry – ", ["formwork", "roof trusses", "interior walls", "window frames", "stairs", "flooring", "kitchen fit-out"], "Carpentry on site, following the site manager's plan.", "Site labourer", MaxExtraPeople: 1),
            new("Electrical installation", "Electrical – ", ["temporary power", "distribution board", "lighting", "cable pulling", "sockets and switches"], "Electrical work by an authorized electrician."),
            new("Scaffolding crew", "Scaffolding – ", ["facade east", "facade west", "stairwell tower", "roof edge", "chimney"], "Scaffolding up, moved or down."),
            new("Forklift driver", "Forklift – ", ["deliveries", "material yard", "unloading"], "Materials moved around the site."),
            new("Site labourer", "Site work – ", ["clearing", "groundwork", "waste sorting", "concrete pour"], "Help for the trades on site.", MaxExtraPeople: 1),
        ],
        Shifts =
        [
            new(new TimeOnly(7, 0), new TimeOnly(16, 0)),
            new(new TimeOnly(7, 0), new TimeOnly(12, 0)),
            new(new TimeOnly(12, 0), new TimeOnly(16, 0)),
            new(new TimeOnly(6, 30), new TimeOnly(15, 30)),
        ],
        WorkingDays = DemoWeek.Weekdays,
        RecurringPercent = 70,
        LongestRecurringWeeks = 10,
        Texts = new(
            "None of our crews with the right papers is free that week, and the site manager was told.",
            "The customer canceled: the delivery of materials is delayed by two weeks.",
            [
                new(false, ["The site moves the gate to the north side from Monday. Please use it.", "Understood, thanks."]),
                new(true, ["Should I bring my own nail gun, or is there one on site?", "There are two on site, in the tool container."]),
                new(true, ["Could I take a long lunch on Wednesday for a meeting at the bank?"]),
            ],
            ["Do I need to bring my own harness for the scaffolding?", "Is there parking for a van at the site?"]),
        ClosedDays =
        [
            new("Safety training day", 15, 15),
            new("Helsingborg office move", 29, 30, Helsingborg),
        ],
        ClosedOnPublicHolidays = true,
    };
}
