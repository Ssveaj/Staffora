using Staffora.Core.Entities.Organization;
using static Staffora.Services.Demo.DemoRequirement;

namespace Staffora.Services.Demo;

public static class NordvaktDemo
{
    public const string TenantName = "Nordvakt Security";

    public const string PortalCustomer = "Arena Norr Events";

    private const string Domain = "nordvakt.example.com";

    private const string Umea = "Umeå";

    private const string Lulea = "Luleå";

    private const string Sundsvall = "Sundsvall";

    private static readonly DemoPerson senior = DemoPeople.Of("Stefan Nyberg", Domain);

    private static readonly DemoPerson[] juniors = [DemoPeople.Of("Jenny Lantto", Domain), DemoPeople.Of("Robin Ahmadi", Domain)];

    public static DemoBusiness Business { get; } = new()
    {
        TenantName = TenantName,
        Trade = "security and events",
        Description = "Guards sites at night and staffs concerts, matches and fairs in Umeå, Luleå and Sundsvall.",
        Seed = 8_905,
        Domain = Domain,
        Owners = [DemoPeople.Of("Mats Westerlund", Domain), DemoPeople.Of("Sanna Isaksson", Domain)],
        SeniorCoordinator = senior,
        JuniorCoordinators = juniors,
        Units =
        [
            new(Umea, OrganizationUnitType.Branch, "The guard room for Umeå.", [senior], PostalArea: "903", Streets: ["Älvbrinken", "Skolgatan Norra", "Björkstigen", "Sandbacksvägen"]),
            new(Lulea, OrganizationUnitType.Branch, "The guard room for Luleå and the coast.", [juniors[0]], PostalArea: "972", Streets: ["Hamngatan Övre", "Malmvägen", "Storgränd", "Bergnäsvägen"]),
            new(Sundsvall, OrganizationUnitType.Branch, "The guard room for Sundsvall.", [juniors[1]], PostalArea: "852", Streets: ["Norrmalmsgränd", "Selångersvägen", "Kajgatan", "Stenstadsvägen"]),
        ],
        Qualifications = ["Security guard licence", "Event security", "First aid", "Dog handler"],
        WorkPatterns =
        [
            new("Evenings", DemoWeek.EveryDay, new TimeOnly(15, 0), new TimeOnly(23, 30)),
            new("Nights", DemoWeek.EveryDay, new TimeOnly(21, 0), new TimeOnly(7, 0)),
            new("Event weekends", DemoWeek.Weekends, new TimeOnly(11, 0), new TimeOnly(23, 30)),
        ],
        StaffNames =
        [
            "Alexander Berg", "Bashir Nur", "Carina Öhman", "Dennis Lund", "Elsa Granlund", "Frida Marklund", "Gustav Lindberg", "Hassan Warsame",
            "Ida Nilsson", "Jesper Holmgren", "Kim Johansson", "Linus Burman", "Moa Sandström", "Nikola Jovanović", "Oscar Larsson", "Petra Lundqvist",
            "Rikard Söderberg", "Sofie Grönlund", "Timo Kallio", "Ulf Eklund", "Vilma Hedman", "Axel Nordlund", "Emilia Brännström", "Kevin Persson",
        ],
        Crews =
        [
            new(Umea, "Nights", ["Security guard licence", "Dog handler"], 2),
            new(Umea, "Evenings", ["Security guard licence", "First aid"], 2),
            new(Umea, "Event weekends", ["Event security", "First aid"], 3),
            new(Umea, "Nights", ["Security guard licence"], 1),
            new(Lulea, "Nights", ["Security guard licence", "First aid"], 2),
            new(Lulea, "Evenings", ["Security guard licence", "Event security"], 2),
            new(Lulea, "Event weekends", ["Event security"], 3),
            new(Sundsvall, "Nights", ["Security guard licence", "Dog handler", "First aid"], 2),
            new(Sundsvall, "Evenings", ["Security guard licence", "Event security", "First aid"], 2),
            new(Sundsvall, "Event weekends", ["Event security", "First aid"], 3),
            new(Sundsvall, "Evenings", ["Security guard licence"], 2),
        ],
        Services =
        [
            new("Site guarding", "A guard on site who checks the gates, the fence and the buildings.", 1, true, [Required("Security guard licence")]),
            new("Event security", "Entrance checks, crowd care and help for the public at an event.", 4, true, [Required("Event security"), Preferred("First aid")]),
            new("Night patrol", "Two guards drive between the customer's sites through the night.", 2, true, [Required("Security guard licence"), Preferred("Dog handler")]),
            new("Dog patrol", "A guard and their dog search and watch a site.", 1, false, [Required("Dog handler"), Required("Security guard licence")]),
            new("First aid post", "A first aider on duty at the event's first aid tent.", 1, true, [Required("First aid")]),
        ],
        Customers =
        [
            new(PortalCustomer, Umea, ["arena", "fairground"]),
            new("Umeå Science Park", Umea, ["main"]),
            new("Norrsken Concert Hall", Lulea, ["main"]),
            new("Luleå Steel Harbour", Lulea, ["gate north", "gate south", "ore yard"]),
            new("Bothnia Ice Hockey Club", Lulea, ["main"]),
            new("Sundsvall Retail Centre", Sundsvall, ["mall", "car park"]),
            new("Medelpad Paper Mill", Sundsvall, ["main"]),
            new("Kvarken Ferries", Umea, ["terminal"]),
        ],
        AccessNotes =
        [
            "Report to the control room on arrival and collect a radio.",
            "The guard hut is by the north gate. The key is in the key safe.",
            "Wear the event vest. The briefing is an hour before the doors open.",
            "Dogs are walked in the yard behind the warehouse only.",
            "Park by the staff entrance. The site's alarm company has the codes.",
        ],
        Portal = new(
            PortalCustomer,
            DemoPeople.Of("Johanna Lindkvist", "arenanorr.example.com"),
            DemoPeople.Of("Felix Ruong", "arenanorr.example.com"),
            new("Winter concert at the arena", "Entrance checks and crowd care for a sold-out concert.", 3, new TimeOnly(17, 0), new TimeOnly(23, 30),
                [new("Event security", 4), new("First aid post", 1)],
                "The doors open at six. Can the team be in place at half past five?",
                "Yes, the briefing is at five and everyone is in place by half past. All five are confirmed."),
            new("Christmas fair, opening weekend", "Guards at the fair's gates and a first aid post.", 8, new TimeOnly(10, 0), new TimeOnly(18, 0),
                [new("Event security", 2), new("First aid post", 1)],
                "There will be a parade at noon. Could one guard walk with it?",
                "We will plan for that. The first aider is confirmed and we are finding the guards.")),
        JobKinds =
        [
            new("Site guarding", "Guarding – ", ["harbour gate", "ore yard", "warehouse", "office block", "building site", "car park"], "A guard on site through the shift.", MaxExtraPeople: 1),
            new("Event security", "Event – ", ["ice hockey match", "concert", "fair", "festival", "conference", "football match", "student party"], "Entrance checks and crowd care.", "First aid post", MaxExtraPeople: 2),
            new("Night patrol", "Night patrol – ", ["route north", "route south", "harbour route", "industrial estate"], "Two guards drive between the sites."),
            new("Dog patrol", "Dog patrol – ", ["timber yard", "rail yard", "storage park"], "A guard and their dog watch the site."),
            new("First aid post", "First aid – ", ["race day", "market", "sports camp"], "A first aider on duty."),
        ],
        Shifts =
        [
            new(new TimeOnly(18, 0), new TimeOnly(23, 0)),
            new(new TimeOnly(22, 0), new TimeOnly(6, 0)),
            new(new TimeOnly(19, 0), new TimeOnly(2, 0)),
            new(new TimeOnly(16, 0), new TimeOnly(22, 0)),
            new(new TimeOnly(20, 0), new TimeOnly(4, 0)),
            new(new TimeOnly(12, 0), new TimeOnly(18, 0)),
            new(new TimeOnly(15, 0), new TimeOnly(23, 0)),
        ],
        WorkingDays = DemoWeek.EveryDay,
        RecurringPercent = 35,
        LongestRecurringWeeks = 8,
        Texts = new(
            "No guard with the right licence was free that night, and the customer was told.",
            "The customer canceled: the event was moved to the spring.",
            [
                new(false, ["The customer added a new gate on the east side. It is on the site map.", "Thanks, I will check it on my first round."]),
                new(true, ["The radio battery at the guard hut is dead. Is there a spare?", "Yes, there are two in the charger in the control room."]),
                new(true, ["Could I swap Saturday night with someone? My daughter has a match."]),
            ],
            ["Is the dog allowed inside the warehouse this time?", "Do I need my own torch, or is there one in the hut?"]),
        ClosedDays =
        [
            new("Licence refresher day", 20, 20, Lulea),
            new("Guard room inspection", 33, 33, Sundsvall),
        ],
    };
}
