using Staffora.Core.Entities.Organization;
using static Staffora.Services.Demo.DemoRequirement;

namespace Staffora.Services.Demo;

public static class LindaleDemo
{
    public const string TenantName = "Lindale School Authority";

    public const string PortalCustomer = "Oakdale Learning";

    public const string Orebro = "Örebro";

    public const string Stockholm = "Stockholm";

    public const string Goteborg = "Göteborg";

    public const string SupportTeam = "Special needs team";

    private const string Domain = "lindale.example.com";

    private static readonly string[] years = ["year 1", "year 2", "year 3", "year 4", "year 5", "year 6", "year 7", "year 8", "year 9"];

    private static readonly DemoPerson senior = DemoPeople.Of("Priya Raman", Domain);

    private static readonly DemoPerson[] juniors = [DemoPeople.Of("Mateo Alvarez", Domain), DemoPeople.Of("Freya Nilsen", Domain)];

    public static DemoBusiness Business { get; } = new()
    {
        TenantName = TenantName,
        Trade = "school staffing",
        Description = "Places qualified staff members in the classrooms of schools in Örebro, Stockholm and Göteborg.",
        Seed = 8_901,
        Domain = Domain,
        Owners = [DemoPeople.Of("Helena Marsh", Domain), DemoPeople.Of("Johan Ekström", Domain)],
        SeniorCoordinator = senior,
        JuniorCoordinators = juniors,
        Units =
        [
            new(Orebro, OrganizationUnitType.Branch, "The office for the schools in and around Örebro.", [juniors[0]], PostalArea: "702", Streets: ["Lärkgatan", "Hagvägen", "Björkallén", "Mjölnargatan"]),
            new(Stockholm, OrganizationUnitType.Branch, "The office for the schools in Stockholm.", [senior], PostalArea: "114", Streets: ["Lindstigen", "Kvarnbacken", "Ekhagsvägen", "Sjöviksgatan"]),
            new(Goteborg, OrganizationUnitType.Branch, "The office for the schools in Göteborg.", [juniors[1]], PostalArea: "412", Streets: ["Havsörnsgatan", "Klippvägen", "Fyrbåksgatan", "Tångvägen"]),
            new(SupportTeam, OrganizationUnitType.Team, "Staff members who give special needs support across Stockholm's schools.", [], Stockholm),
        ],
        Qualifications = ["Mathematics", "Swedish", "Special needs", "Teacher certification"],
        WorkPatterns =
        [
            new("Full days", DemoWeek.Weekdays, new TimeOnly(7, 30), new TimeOnly(16, 0)),
            new("Mornings", DemoWeek.Weekdays, new TimeOnly(7, 30), new TimeOnly(12, 30)),
        ],
        StaffNames =
        [
            "Amara Osei", "Lukas Brenner", "Sofia Marino", "Kenji Watanabe", "Nadia Haddad", "Oliver Grant", "Ingrid Solberg", "Rafael Costa",
            "Meilin Zhou", "Daniel Okoro", "Elin Bergqvist", "Tomas Lindgren", "Hana Kovač", "Isak Holm", "Leyla Demir", "Viktor Sandberg",
            "Maja Åkesson", "Omar Farouk", "Clara Wester", "Anton Fält", "Saga Lund", "Emil Rask", "Yara Haddad", "Noah Pettersson",
        ],
        Crews =
        [
            new(Orebro, "Full days", ["Mathematics", "Teacher certification"], 3),
            new(Orebro, "Full days", ["Swedish", "Teacher certification"], 2),
            new(Orebro, "Mornings", ["Mathematics", "Swedish"], 2),
            new(Stockholm, "Full days", ["Mathematics", "Teacher certification"], 3),
            new(Stockholm, "Full days", ["Swedish", "Teacher certification"], 2),
            new(Stockholm, "Mornings", ["Swedish"], 1),
            new(SupportTeam, "Full days", ["Special needs", "Teacher certification"], 2),
            new(SupportTeam, "Full days", ["Special needs"], 2),
            new(Goteborg, "Full days", ["Mathematics", "Swedish", "Teacher certification"], 2),
            new(Goteborg, "Full days", ["Mathematics", "Teacher certification"], 2),
            new(Goteborg, "Mornings", ["Special needs", "Swedish"], 2),
            new(Goteborg, "Full days", ["Swedish", "Teacher certification"], 1),
        ],
        Services =
        [
            new("Mathematics teaching", "Mathematics for a class, following the school's plan.", 1, true, [Required("Mathematics"), Required("Teacher certification")]),
            new("Swedish teaching", "Swedish for a class, following the school's plan.", 1, true, [Required("Swedish"), Required("Teacher certification")]),
            new("Classroom assistant", "Supports a class and its staff in the room, usually two to a class.", 2, true, [Preferred("Special needs")]),
            new("Special needs support", "One-to-one or small-group support for students with special needs.", 1, false, [Required("Special needs")]),
            new("Substitute cover", "Takes a class while its own staff member is away, in any subject.", 1, false, [Required("Teacher certification")]),
        ],
        Customers =
        [
            new(PortalCustomer, Stockholm, ["main building", "annex"]),
            new("Birchvale School", Orebro, ["main"]),
            new("Sunfield High School", Goteborg, ["building C", "science wing"]),
            new("Northbrook Academy", Orebro, ["north campus", "south campus"]),
            new("Riverside Learning Centre", Stockholm, ["main"]),
            new("Hilltop Academy", Goteborg, ["main"]),
            new("Maple Grove Primary", Stockholm, ["lower years", "upper years", "sports hall"]),
            new("Lakeview College", Orebro, ["main"]),
        ],
        AccessNotes =
        [
            "Sign in at reception and collect a visitor badge.",
            "Use the side entrance. Reception gives out the door code.",
            "Staff entrance on the east side. The caretaker has the keys.",
            "Park behind the sports hall. The staff room is on the first floor.",
            "Lab coats are in the prep room, and the science staff have the key.",
        ],
        Portal = new(
            PortalCustomer,
            DemoPeople.Of("Beatrice Holloway", "oakdale.example.com"),
            DemoPeople.Of("Kwame Mensah", "oakdale.example.com"),
            new("Theme day: mathematics, year 6", "A day of problem solving with games and outdoor measuring.", 2, new TimeOnly(8, 30), new TimeOnly(14, 30),
                [new("Mathematics teaching", 1), new("Classroom assistant", 2)],
                "Could the staff bring measuring tapes for the outdoor tasks?",
                "Yes, they bring a set of tapes. Everyone is confirmed for the day."),
            new("Language day, year 5", "Reading, writing and drama in Swedish for two classes.", 6, new TimeOnly(8, 30), new TimeOnly(14, 0),
                [new("Swedish teaching", 1), new("Classroom assistant", 2)],
                "Our drama room is free all day. Could the group use it after lunch?",
                "Thank you, we will plan for the drama room. One assistant is confirmed and we are finding the second.")),
        JobKinds =
        [
            new("Mathematics teaching", "Mathematics, ", years, "Mathematics for the class, following the school's plan.", "Classroom assistant"),
            new("Swedish teaching", "Swedish, ", years, "Swedish for the class: reading, writing and speaking."),
            new("Classroom assistant", "Classroom assistant, ", years, "Support in the classroom for the class and its staff.", MaxExtraPeople: 1),
            new("Special needs support", "Special needs support, ", years, "Support for students with special needs, in the class or in a small group."),
            new("Substitute cover", "Substitute, ", years, "Takes the class while its own staff member is away."),
        ],
        Shifts =
        [
            new(new TimeOnly(8, 0), new TimeOnly(9, 30)),
            new(new TimeOnly(8, 30), new TimeOnly(10, 0)),
            new(new TimeOnly(9, 45), new TimeOnly(11, 15)),
            new(new TimeOnly(10, 15), new TimeOnly(11, 45)),
            new(new TimeOnly(11, 30), new TimeOnly(12, 30)),
            new(new TimeOnly(13, 0), new TimeOnly(14, 30)),
            new(new TimeOnly(13, 15), new TimeOnly(14, 45)),
            new(new TimeOnly(14, 45), new TimeOnly(15, 45)),
        ],
        WorkingDays = DemoWeek.Weekdays,
        RecurringPercent = 40,
        LongestRecurringWeeks = 8,
        Texts = new(
            "No staff member with the right qualifications is free on those days, and the school was told.",
            "The school canceled: the class is away on a field trip that week.",
            [
                new(false, ["The class has a plan in the shared folder. Please follow it for the first week.", "Thank you, I have read it and will follow it."]),
                new(true, ["Is there a projector in the room, or should I bring an adapter for my laptop?", "There is a projector, and an adapter is in the staff room."]),
                new(true, ["Could I leave fifteen minutes early on Thursday for a dentist's appointment?"]),
            ],
            ["Is the class's plan for the week in the shared folder, or should I ask the school?", "Could I start at nine instead of eight that day? I come from another school."]),
        ClosedDays =
        [
            new("Founders' Day", 25, 25),
            new("Göteborg study days", 22, 23, Goteborg),
        ],
        ClosedOnPublicHolidays = true,
    };
}
