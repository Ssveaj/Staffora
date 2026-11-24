# Demo data

## Demo data

A development database gets a demo that looks current and covers what the product does in five trades. It is built at startup through the services people use, so every row carries the history, stamps and notices the app would give it, and nothing is written by hand except the error log rows described below.

### When it runs

- `Program.cs` runs it in Development only, and only when startup seeding ran (not with `STAFFORA_E2E_SKIP_STARTUP_SEED`).
- It runs after the seeder (`DatabaseSeeder`, whose Development data is the platform tenant and its seeded owner alone, and which answers that owner, or the owner the platform already had) and the startup ensures.
- `DemoDataSeeder` (`Staffora.Services/Demo/`, scoped) builds every demo business whose tenant name no tenant has yet, as that platform owner and the people it onboards.
- A demo tenant that exists is left as it is (logged), so a second start adds nothing. A start that fails half-way stops with its reason and keeps what it built; building the demo anew takes a fresh database.
- In any other environment `SeedAsync` refuses (`NotDevelopment`) before it reads or writes anything; startup never calls it there.
- A development database with older demo tenants keeps them; only the companies whose tenant name no tenant has yet are built beside them. Drop the database and start the app to get the current demo.
- The first Development start on a fresh database takes about 70 seconds (each company about 13 seconds); later starts find the tenants and build nothing.

### Who acts

- Each person acts in a service scope of their own, signed in as a new circuit would be (`DemoSessions`). The cookie principal the app's sign-in manager would issue becomes the scope's authentication state, and `UserSessionService` resolves the session from it and the database. Every step is judged by its permission, workflow and tenant as in the app.
- No password is checked to sign in this way, so a demo session opens only where the host says it runs in Development. It refuses before it reads anything otherwise, a host that names no environment included (`DemoSessions.NotDevelopment`). The end-to-end preparation's services are a Development host, as is the server they prepare for.
- Nothing a request reaches names the demo; only `Program.cs`'s Development block, the end-to-end preparation and tests call it (`DemoScanTests`).

### Five companies, as data

Each company is a `DemoBusiness` in a file of its own (`LindaleDemo`, `ClearshineDemo`, `NordbyggDemo`, `OmsorgenDemo`, `NordvaktDemo`), written as compact tables:

- its two system owners, its senior and two junior coordinators;
- its units (three cities, each with its postal area and a few fictional streets, and Lindale's Special needs team below Stockholm), with the coordinator placed on each;
- its qualifications and work patterns (an overnight one as two segments a day);
- its staff as crews (a unit, a pattern, the qualifications and how many, named from a list of names);
- its four to six services and its eight customers (each on a city, with one to three sites), its access notes;
- the portal customer with its two showcase orders;
- the kinds of job it does, the times of day and the days of the week it works, how many of its jobs recur and for how long;
- its closed days, whether it closes on public holidays, and its own words for cancellations and conversations.

Places are made from those tables with the company's fixed seed: a street and number, a Swedish postal code (`702 15`) and the city. An email address is `first.last@` the company's domain without accents.

`DemoTenantBuilder` builds one company through the services, one company after the other. Each person acts in one cached session for the whole build.

1. **Onboarding.** The platform owner onboards the tenant with its first system owner, who sets the demo's password. That owner, in Setup:
   - adds the units;
   - creates the roles **Senior coordinator** (the Coordinator preset's permissions with `RolesView` and `RolesEdit`) and **Junior coordinator** (the Coordinator preset without `ManageWorkPatterns` and `AssignWorkPatterns`);
   - creates the second system owner (the System owner role on the root), the coordinators and the staff members (the Staff preset on the staff member's unit, [ADR 0008](../adr/0008-users-become-resources-through-a-role.md) and [ADR 0021](../adr/0021-one-organization-tree-per-tenant.md));
   - adds the closed days, turns `General.ClosedOnPublicHolidays` on through `TenantSettingsService` at Lindale and Nordbygg, and adds and gives the qualifications.

   The platform owner then activates the tenant.
2. **Setting up.** The senior coordinator gives the second junior coordinator their role, publishes the work patterns and gives one to each staff member, and adds the customers. The first owner adds the service catalogue and enables the portal customer's portal, its orders going to the customer's city. Its customer administrator creates the orderer. The dispatcher carries the offered services into the portal.
3. **Portal orders.** The orderer sends the two showcase orders with a question in each thread, and three more orders that wait for an answer (their jobs stay Awaiting coordination). The coordinator of the portal customer's city starts the two showcase jobs, staffs the first and confirms it, staffs the second in part and asks a staff member for another slot, and answers both threads.
4. **Jobs.** The coordinators register 104 jobs from a script of fates. Each job's kind, name, customer, place, time, single or recurring, days and crew size are drawn from a `Random` with the company's seed, every day relative to the injected clock, from four weeks back to eight weeks ahead:
   - 20 completed (single jobs in the last four weeks, recurring ones that ended last week), 5 in progress, 25 staffed and confirmed, 2 staffed and not confirmed;
   - a first-to-accept round that filled its slot group (its other requests not selected), an open round, a round with first to accept off and two yeses waiting for the pick, a single yes waiting for the pick;
   - two staff questions, four requests waiting for an answer and two whose answer-by time is four hours away, an expired round and an expired request;
   - 14 partly staffed, 12 uncovered and 4 uncovered within the next two days;
   - two canceled by the company and two by the customer with their reasons, two in registration and two handed over.

   Rules the build follows:
   - Each job sits on its customer's city and is registered by that city's coordinator, who staffs it preferring staff members whose work pattern covers it and who work in the same city.
   - A slot is only asked of or given to a staff member who holds the service's Required qualifications and is free then (the demo's own conservative booking beside the assignment's own check). A plan the free staff cannot fill is drawn again, up to 20 times.
   - Days come from the company's working days without its closed days and, at Lindale and Nordbygg, without the Swedish public holidays and eves (`TenantHolidays`). A recurring job takes only weekdays none of whose remaining dates is closed.
   - The staff members answer their own requests and write their own questions.
   - An expired request is sent as it would have been three days ago: the request step runs in a `SlotAssignmentService` or `RequestRoundService` whose `WorkflowService` reads a clock three days back (`DemoSentEarlier`), with a day to answer, so every rule is judged at its sending time, and the job clock's pass expires it by its own system step.
5. **Conversations and absence.** Coordinators and staff members talk on three assignments (one waiting for the coordinator's reply). Sixteen absences are written by the staff members and the coordinators:
   - a leave the senior coordinator registers (approved at once, as a registration by an approver is);
   - two leaves reported and approved;
   - three sick leaves in the past (one registered and handled at once, two reported and marked Handled);
   - a rejected leave with its reason, three leaves waiting for approval;
   - a question from a coordinator to a staff member and one from a staff member to the coordinators;
   - three sick leaves from today waiting to be handled (one over the staff member's assigned work);
   - a sick leave a junior coordinator registers over a staff member's assignment in the coming days, after which the job's coordinator takes the staff member off for absence and the slot is open again.
6. **Finishing.** The job clock's pass (run twice) puts the jobs whose period began in progress, completes those that ended and expires the requests whose answer-by time passed. The dispatcher carries what that told the portals.
7. **Error log.** The error log gets a month of rows (`DemoErrorLog`), so Platform health has something to show: a handful of recurring issues at times drawn from a `Random` with a fixed seed relative to the clock, naming Lindale's and Clearshine's tenants and people. They are the one thing the demo writes by hand, on a system context under a logged lease, since the real logging path cannot date a row back.

### The companies

- **Lindale School Authority** (schools: Örebro, Stockholm with its Special needs team, Göteborg). Customers Oakdale Learning (portal, Stockholm), Birchvale School, Sunfield High School and five more. Qualifications Mathematics, Swedish, Special needs and Teacher certification. Services Mathematics teaching, Swedish teaching, Classroom assistant (two people), Special needs support and Substitute cover. Patterns Full days and Mornings on weekdays. Closed days: "Founders' Day" for the whole organization and "Göteborg study days" for Göteborg. Closed on public holidays.
- **Clearshine Cleaning Services** (cleaning: Stockholm, Södertälje, Norrtälje). Customers Hagen Bakery (portal), Ling & Co Accounting and six more. Qualifications Office cleaning, Window cleaning, Floor care and Hygiene training. Services Office cleaning, Deep cleaning, Window cleaning, Floor polishing and Cleaning supervisor. Patterns Early shift, Evening shift and Mornings, Tuesday to Saturday; early mornings and evenings, Saturdays too. Closed days: a depot inventory day in Södertälje and a staff day.
- **Nordbygg Construction** (building: Malmö, Lund, Helsingborg). Customers Kvarnby Housing (portal) and seven more. Qualifications Carpenter, Electrician authorization, Scaffolding, Forklift licence and ID06 card. Services Carpentry, Electrical installation, Scaffolding crew, Forklift driver and Site labourer. Patterns Day shift and Early day shift, 07 to 16 on weekdays, most jobs recurring for up to ten weeks. Closed days: a safety training day and the Helsingborg office move. Closed on public holidays.
- **Omsorgen Home Care** (home care: Uppsala, Västerås, Gävle). Customers Solbacken Senior Living (portal) and seven more. Qualifications Assistant nurse, Medication delegation, Dementia care and Driving licence B. Services Home care visit, Medication round, Dementia support, Night check and Two-person transfer. Patterns Days, Evenings and Nights every day; short visits around the clock, weekends too. Closed days: a team training day in Gävle and a care planning day in Uppsala.
- **Nordvakt Security** (security and events: Umeå, Luleå, Sundsvall). Customers Arena Norr Events (portal) and seven more. Qualifications Security guard licence, Event security, First aid and Dog handler. Services Site guarding, Event security (four people), Night patrol, Dog patrol and First aid post. Patterns Evenings, Nights and Event weekends; evenings, nights across midnight and events. Closed days: a licence refresher day in Luleå and a guard room inspection in Sundsvall.

### Who signs in

The passwords are written in the source, set only in Development and never logged: `DemoDataSeeder.OwnerPassword` for the system owners and the customer administrators, `MemberPassword` for everyone else (`DemoDataSeeder.PasswordOf`). An issued initial password is changed to them through Identity's own change (`UserManager.ChangePasswordAsync`), a seeding shortcut of Development, since no page lets anyone change their own password ([ADR 0034](../adr/0034-no-self-service-account-pages.md)). Every name is fictional ([PRODUCT.md](../../PRODUCT.md), "Words"); the cities are real and the streets made up.

| Tenant | Role | Name | Email | Password |
|---|---|---|---|---|
| Platform | Platform owner | Platform Owner | `admin@staffora.system` | the seeder's (`DatabaseSeeder`) |
| Lindale School Authority | System owner | Helena Marsh | `helena.marsh@lindale.example.com` | `OwnerPassword` |
| Lindale School Authority | System owner | Johan Ekström | `johan.ekstrom@lindale.example.com` | `OwnerPassword` |
| Lindale School Authority | Senior coordinator | Priya Raman | `priya.raman@lindale.example.com` | `MemberPassword` |
| Lindale School Authority | Junior coordinator | Mateo Alvarez | `mateo.alvarez@lindale.example.com` | `MemberPassword` |
| Lindale School Authority | Junior coordinator | Freya Nilsen | `freya.nilsen@lindale.example.com` | `MemberPassword` |
| Lindale School Authority | Staff (24) | Amara Osei, Lukas Brenner, Sofia Marino, Kenji Watanabe, Nadia Haddad, Oliver Grant, Ingrid Solberg, Rafael Costa, Meilin Zhou, Daniel Okoro, Elin Bergqvist, Tomas Lindgren, Hana Kovač, Isak Holm, Leyla Demir, Viktor Sandberg, Maja Åkesson, Omar Farouk, Clara Wester, Anton Fält, Saga Lund, Emil Rask, Yara Haddad, Noah Pettersson | `first.last@lindale.example.com` without accents (`amara.osei@lindale.example.com`, Full days) | `MemberPassword` |
| Oakdale Learning (portal) | Customer administrator | Beatrice Holloway | `beatrice.holloway@oakdale.example.com` | `OwnerPassword` |
| Oakdale Learning (portal) | Orderer | Kwame Mensah | `kwame.mensah@oakdale.example.com` | `MemberPassword` |
| Clearshine Cleaning Services | System owner | Grace Mwangi | `grace.mwangi@clearshine.example.com` | `OwnerPassword` |
| Clearshine Cleaning Services | System owner | Lars Öberg | `lars.oberg@clearshine.example.com` | `OwnerPassword` |
| Clearshine Cleaning Services | Senior coordinator | Henrik Dahl | `henrik.dahl@clearshine.example.com` | `MemberPassword` |
| Clearshine Cleaning Services | Junior coordinator | Leila Farah | `leila.farah@clearshine.example.com` | `MemberPassword` |
| Clearshine Cleaning Services | Junior coordinator | Connor Byrne | `connor.byrne@clearshine.example.com` | `MemberPassword` |
| Clearshine Cleaning Services | Staff (24) | Ana Petrovic, Juan Morales, Fatima Bakr, Piotr Nowak, Aisha Bello, Erik Strand, Mina Rahimi, Tobias Weller, Yuki Sato, Samuel Adeyemi, Linnea Kask, Dragan Ilić, Rosa Medina, Kofi Asante, Agnes Wikström, Bilal Hussein, Tove Lindahl, Marek Kowalski, Sara Engel, Ibrahim Diallo, Alma Nyström, Pavel Novák, Nora Blom, Ali Karimi | `first.last@clearshine.example.com` without accents (`ana.petrovic@clearshine.example.com`, Early shift) | `MemberPassword` |
| Hagen Bakery (portal) | Customer administrator | Lotte Hagen | `lotte.hagen@hagenbakery.example.com` | `OwnerPassword` |
| Hagen Bakery (portal) | Orderer | Marco Bianchi | `marco.bianchi@hagenbakery.example.com` | `MemberPassword` |
| Nordbygg Construction | System owner | Karin Nordqvist | `karin.nordqvist@nordbygg.example.com` | `OwnerPassword` |
| Nordbygg Construction | System owner | Fredrik Malm | `fredrik.malm@nordbygg.example.com` | `OwnerPassword` |
| Nordbygg Construction | Senior coordinator | Mikael Sjöberg | `mikael.sjoberg@nordbygg.example.com` | `MemberPassword` |
| Nordbygg Construction | Junior coordinator | Ayşe Yilmaz | `ayse.yilmaz@nordbygg.example.com` | `MemberPassword` |
| Nordbygg Construction | Junior coordinator | Patrik Holmén | `patrik.holmen@nordbygg.example.com` | `MemberPassword` |
| Nordbygg Construction | Staff (24) | Björn Lindqvist, Dariusz Wójcik, Emma Rosén, Goran Petrović, Hampus Ek, Ismail Osman, Jonas Strömberg, Kalle Hedlund, Lina Forsberg, Magnus Berglund, Nils Ahlgren, Oskar Lundin, Per Sundqvist, Rasmus Kjellberg, Sven Håkansson, Teodor Wiklund, Ulrik Dahlin, Vera Lindström, Wilhelm Brandt, Zoran Marković, Adam Nordin, Beatrice Falk, Calle Svensson, David Engström | `first.last@nordbygg.example.com` without accents (`bjorn.lindqvist@nordbygg.example.com`, Day shift) | `MemberPassword` |
| Kvarnby Housing (portal) | Customer administrator | Ingela Kvarnström | `ingela.kvarnstrom@kvarnby.example.com` | `OwnerPassword` |
| Kvarnby Housing (portal) | Orderer | Tarik Hasanović | `tarik.hasanovic@kvarnby.example.com` | `MemberPassword` |
| Omsorgen Home Care | System owner | Birgitta Lönn | `birgitta.lonn@omsorgen.example.com` | `OwnerPassword` |
| Omsorgen Home Care | System owner | Ahmed Saleh | `ahmed.saleh@omsorgen.example.com` | `OwnerPassword` |
| Omsorgen Home Care | Senior coordinator | Annika Wallin | `annika.wallin@omsorgen.example.com` | `MemberPassword` |
| Omsorgen Home Care | Junior coordinator | Samir Haddou | `samir.haddou@omsorgen.example.com` | `MemberPassword` |
| Omsorgen Home Care | Junior coordinator | Lovisa Grahn | `lovisa.grahn@omsorgen.example.com` | `MemberPassword` |
| Omsorgen Home Care | Staff (24) | Ebba Sjödin, Fatou Ceesay, Gunnel Persson, Hiba Mansour, Ines Andersson, Josefin Lilja, Khadija Ali, Lena Viklund, Matilda Ström, Nasrin Rostami, Olga Ivanova, Pia Hellström, Rut Eriksson, Selma Nord, Tilde Hammar, Ulla Björk, Valentina Rossi, William Ek, Amir Jafari, Bengt Olsson, Cecilia Moreno, Dana Abdi, Edvin Hult, Filippa Sund | `first.last@omsorgen.example.com` without accents (`ebba.sjodin@omsorgen.example.com`, Days) | `MemberPassword` |
| Solbacken Senior Living (portal) | Customer administrator | Margareta Stenlund | `margareta.stenlund@solbacken.example.com` | `OwnerPassword` |
| Solbacken Senior Living (portal) | Orderer | Emir Kovačević | `emir.kovacevic@solbacken.example.com` | `MemberPassword` |
| Nordvakt Security | System owner | Mats Westerlund | `mats.westerlund@nordvakt.example.com` | `OwnerPassword` |
| Nordvakt Security | System owner | Sanna Isaksson | `sanna.isaksson@nordvakt.example.com` | `OwnerPassword` |
| Nordvakt Security | Senior coordinator | Stefan Nyberg | `stefan.nyberg@nordvakt.example.com` | `MemberPassword` |
| Nordvakt Security | Junior coordinator | Jenny Lantto | `jenny.lantto@nordvakt.example.com` | `MemberPassword` |
| Nordvakt Security | Junior coordinator | Robin Ahmadi | `robin.ahmadi@nordvakt.example.com` | `MemberPassword` |
| Nordvakt Security | Staff (24) | Alexander Berg, Bashir Nur, Carina Öhman, Dennis Lund, Elsa Granlund, Frida Marklund, Gustav Lindberg, Hassan Warsame, Ida Nilsson, Jesper Holmgren, Kim Johansson, Linus Burman, Moa Sandström, Nikola Jovanović, Oscar Larsson, Petra Lundqvist, Rikard Söderberg, Sofie Grönlund, Timo Kallio, Ulf Eklund, Vilma Hedman, Axel Nordlund, Emilia Brännström, Kevin Persson | `first.last@nordvakt.example.com` without accents (`alexander.berg@nordvakt.example.com`, Nights) | `MemberPassword` |
| Arena Norr Events (portal) | Customer administrator | Johanna Lindkvist | `johanna.lindkvist@arenanorr.example.com` | `OwnerPassword` |
| Arena Norr Events (portal) | Orderer | Felix Ruong | `felix.ruong@arenanorr.example.com` | `MemberPassword` |

### Not the journeys' tenant

The browser journeys' Company A with Brightpath Learning and its busy week (`CompanyASetup`, `CoordinationShowcase`, run by `JourneyTenantFixture` and `CoordinationShowcaseFixture` in Staffora.E2ESetup with the bootstrapped platform owner and no chosen password) stay in `Staffora.Services/Demo/` for the end-to-end preparation; the demo does not build them ([E2E README](../../Staffora.Test/E2E/README.md)).

### Verified by

- `DemoDataTests`, on the real app with its clock anchored on a fixed Monday, over one Development start on a fresh database of its own (`DemoDataTests.DevelopmentStart`): every company's people, units, customers and at least 100 jobs; jobs, requests, rounds, orders and absence in every state listed above; public holidays and closed days; Lindale in depth (workspace, permissions, threads, portal, rounds and expiry); every demo person signs in with the demo's password, which no log line names; a second start changes nothing; no tenant or user is seeded outside Development, and a Development start with the end-to-end gate set builds nothing.
- `DemoScanTests`: the demo is named only by the startup's Development block, a session is refused outside Development before it reads, and this table names every sign-in.
- `DatabaseSeederTests`, `PlatformBootstrapTests`, `StartupSeedGateTests`, `FoundationPrepTests`: the seeder's Development data is the platform alone, the startup gate and the shared setup.
