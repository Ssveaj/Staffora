using Staffora.Core.Entities.Organization;
using static Staffora.Services.Demo.DemoRequirement;

namespace Staffora.Services.Demo;

public static class OmsorgenDemo
{
    public const string TenantName = "Omsorgen Home Care";

    public const string PortalCustomer = "Solbacken Senior Living";

    private const string Domain = "omsorgen.example.com";

    private const string Uppsala = "Uppsala";

    private const string Vasteras = "Västerås";

    private const string Gavle = "Gävle";

    private static readonly DemoPerson senior = DemoPeople.Of("Annika Wallin", Domain);

    private static readonly DemoPerson[] juniors = [DemoPeople.Of("Samir Haddou", Domain), DemoPeople.Of("Lovisa Grahn", Domain)];

    public static DemoBusiness Business { get; } = new()
    {
        TenantName = TenantName,
        Trade = "home care",
        Description = "Sends assistant nurses on home care visits around the clock in Uppsala, Västerås and Gävle.",
        Seed = 8_904,
        Domain = Domain,
        Owners = [DemoPeople.Of("Birgitta Lönn", Domain), DemoPeople.Of("Ahmed Saleh", Domain)],
        SeniorCoordinator = senior,
        JuniorCoordinators = juniors,
        Units =
        [
            new(Uppsala, OrganizationUnitType.Branch, "The home care team for Uppsala.", [senior], PostalArea: "753", Streets: ["Svartbäcksvägen", "Kungsängsgränd", "Fyrisstigen", "Linnévägen"]),
            new(Vasteras, OrganizationUnitType.Branch, "The home care team for Västerås.", [juniors[0]], PostalArea: "722", Streets: ["Mälarvägen", "Svartågatan", "Kopparbergsgränd", "Hamnvägen"]),
            new(Gavle, OrganizationUnitType.Branch, "The home care team for Gävle.", [juniors[1]], PostalArea: "803", Streets: ["Gavleåvägen", "Brynäsgatan", "Sätragränd", "Stigslundsvägen"]),
        ],
        Qualifications = ["Assistant nurse", "Medication delegation", "Dementia care", "Driving licence B"],
        WorkPatterns =
        [
            new("Days", DemoWeek.EveryDay, new TimeOnly(7, 0), new TimeOnly(15, 0)),
            new("Evenings", DemoWeek.EveryDay, new TimeOnly(14, 0), new TimeOnly(22, 0)),
            new("Nights", DemoWeek.EveryDay, new TimeOnly(21, 30), new TimeOnly(7, 30)),
        ],
        StaffNames =
        [
            "Ebba Sjödin", "Fatou Ceesay", "Gunnel Persson", "Hiba Mansour", "Ines Andersson", "Josefin Lilja", "Khadija Ali", "Lena Viklund",
            "Matilda Ström", "Nasrin Rostami", "Olga Ivanova", "Pia Hellström", "Rut Eriksson", "Selma Nord", "Tilde Hammar", "Ulla Björk",
            "Valentina Rossi", "William Ek", "Amir Jafari", "Bengt Olsson", "Cecilia Moreno", "Dana Abdi", "Edvin Hult", "Filippa Sund",
        ],
        Crews =
        [
            new(Uppsala, "Days", ["Assistant nurse", "Medication delegation", "Driving licence B"], 3),
            new(Uppsala, "Evenings", ["Assistant nurse", "Dementia care"], 2),
            new(Uppsala, "Nights", ["Assistant nurse", "Medication delegation"], 2),
            new(Uppsala, "Days", ["Assistant nurse", "Dementia care", "Driving licence B"], 1),
            new(Vasteras, "Days", ["Assistant nurse", "Driving licence B"], 3),
            new(Vasteras, "Evenings", ["Assistant nurse", "Medication delegation", "Dementia care"], 2),
            new(Vasteras, "Nights", ["Assistant nurse"], 2),
            new(Gavle, "Days", ["Assistant nurse", "Medication delegation", "Driving licence B"], 2),
            new(Gavle, "Evenings", ["Assistant nurse", "Dementia care", "Driving licence B"], 3),
            new(Gavle, "Nights", ["Assistant nurse", "Medication delegation"], 2),
            new(Gavle, "Days", ["Assistant nurse", "Dementia care"], 2),
        ],
        Services =
        [
            new("Home care visit", "Personal care, meals and a talk, at the client's home.", 1, true, [Required("Assistant nurse"), Preferred("Driving licence B")]),
            new("Medication round", "Gives the client's medication as prescribed and signs the list.", 1, true, [Required("Assistant nurse"), Required("Medication delegation")]),
            new("Dementia support", "Company and care for a client living with dementia.", 1, true, [Required("Dementia care")]),
            new("Night check", "A quiet visit at night to check that the client is well.", 1, false, [Required("Assistant nurse")]),
            new("Two-person transfer", "Two staff members help the client out of bed or into the shower with a lift.", 2, true, [Required("Assistant nurse")]),
        ],
        Customers =
        [
            new(PortalCustomer, Uppsala, ["house 1", "house 2"]),
            new("Fyris Care Partners", Uppsala, ["client homes north", "client homes south"]),
            new("Luthagen Family Care", Uppsala, ["main"]),
            new("Mälardalen Care Trust", Vasteras, ["client homes west", "client homes east", "day centre"]),
            new("Björkbacken Home Services", Vasteras, ["main"]),
            new("Gävle Bay Care", Gavle, ["client homes", "respite house"]),
            new("Norrlandia Assisted Living", Gavle, ["main"]),
            new("Svea Private Care", Vasteras, ["main"]),
        ],
        AccessNotes =
        [
            "The key box is by the front door. The code is in the client's care plan.",
            "Ring twice and wait. The client is slow to reach the door.",
            "Shoe covers are in the hall. Sign the visit log on the kitchen table.",
            "The night entrance is round the back. The staff room has the keys.",
            "Park in the visitors' spaces. The lift goes to the third floor only.",
        ],
        Portal = new(
            PortalCustomer,
            DemoPeople.Of("Margareta Stenlund", "solbacken.example.com"),
            DemoPeople.Of("Emir Kovačević", "solbacken.example.com"),
            new("Extra evening staff for the autumn party", "Two assistant nurses help the residents at the party and to bed afterwards.", 2, new TimeOnly(16, 0), new TimeOnly(21, 0),
                [new("Home care visit", 2)],
                "Could the staff help with the wheelchairs on the way to the hall?",
                "Of course, they plan for it. Both are confirmed."),
            new("Weekend cover for house 2", "Medication rounds and visits while our own staff are on a course.", 5, new TimeOnly(7, 30), new TimeOnly(12, 0),
                [new("Medication round", 1), new("Home care visit", 2)],
                "The medication cabinet has a new code from Friday. Who should we send it to?",
                "Please send it to the coordinator, who passes it on. The medication round is confirmed and we are finding the visits.")),
        JobKinds =
        [
            new("Home care visit", "Home visit – ", ["morning care", "lunch", "afternoon coffee", "shopping", "evening meal", "shower help", "walk", "bedtime"], "Personal care and help at home, following the care plan.", MaxExtraPeople: 1),
            new("Medication round", "Medication – ", ["morning", "noon", "evening", "bedtime"], "Medication given as prescribed and signed for.", "Home care visit"),
            new("Dementia support", "Dementia support – ", ["day company", "evening company", "outing", "memory café"], "Company and care for a client living with dementia."),
            new("Night check", "Night check – ", ["first round", "second round", "early morning"], "A quiet check that the client is well."),
            new("Two-person transfer", "Transfer – ", ["morning lift", "shower lift", "evening lift"], "Two staff members help the client with a lift."),
        ],
        Shifts =
        [
            new(new TimeOnly(7, 30), new TimeOnly(8, 15)),
            new(new TimeOnly(8, 30), new TimeOnly(9, 30)),
            new(new TimeOnly(11, 30), new TimeOnly(12, 15)),
            new(new TimeOnly(14, 30), new TimeOnly(15, 30)),
            new(new TimeOnly(17, 0), new TimeOnly(17, 45)),
            new(new TimeOnly(20, 30), new TimeOnly(21, 15)),
            new(new TimeOnly(23, 0), new TimeOnly(23, 30)),
            new(new TimeOnly(2, 0), new TimeOnly(2, 30)),
            new(new TimeOnly(5, 30), new TimeOnly(6, 15)),
        ],
        WorkingDays = DemoWeek.EveryDay,
        RecurringPercent = 55,
        LongestRecurringWeeks = 8,
        Texts = new(
            "No nurse with medication delegation was free for that time, and the client's family was told.",
            "The customer canceled: the client is in hospital that week.",
            [
                new(false, ["The client's care plan changed on Monday. Please read it before the visit.", "Thank you, I have read it."]),
                new(true, ["The key box code did not work this morning. Has it changed?", "Yes, the family changed it. The new code is in the care plan."]),
                new(true, ["Could I come half an hour later on Sunday? The bus does not run that early."]),
            ],
            ["Does the client need help with the shower on this visit too?", "Is there somewhere to park the car near the house?"]),
        ClosedDays =
        [
            new("Gävle team training", 19, 19, Gavle),
            new("Care planning day", 26, 26, Uppsala),
        ],
    };
}
