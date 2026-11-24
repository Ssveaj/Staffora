using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Settings;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class ReadSweepTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public ReadSweepTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Read_as_A_no_tenant_table_returns_a_row_of_B()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await SeedEveryTenantTableAsync(scenario.A, scenario.B.TenantId, cancellationToken);
        await SeedEveryTenantTableAsync(scenario.B, scenario.A.TenantId, cancellationToken);

        var unseeded = new List<string>();
        var leaked = new List<string>();
        await using var system = this.database.CreateSystemContext();
        await using var readerA = this.database.CreateContext(scenario.A.Runtime());
        var tenantTypes = system.Model.GetEntityTypes().Where(type => typeof(ITenantEntity).IsAssignableFrom(type.ClrType)).ToList();
        foreach (var entityType in tenantTypes)
        {
            var stored = await TenantIdsAsync(system, entityType.ClrType, cancellationToken);
            var seenByA = await TenantIdsAsync(readerA, entityType.ClrType, cancellationToken);
            var name = entityType.ClrType.Name;

            if (!stored.Contains(scenario.A.TenantId) || !stored.Contains(scenario.B.TenantId))
            {
                unseeded.Add(name);
            }

            if (!seenByA.Contains(scenario.A.TenantId) || seenByA.Any(tenantId => tenantId != scenario.A.TenantId))
            {
                leaked.Add($"{name}: A read the tenants [{string.Join(", ", seenByA.Distinct())}]");
            }
        }

        Assert.Contains(tenantTypes, type => type.ClrType == typeof(TestGrandchild));
        Assert.Empty(unseeded);
        Assert.Empty(leaked);
    }

    internal async Task SeedEveryTenantTableAsync(ScenarioTenant tenant, Guid otherTenant, CancellationToken cancellationToken)
    {
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, tenant, cancellationToken);
        await using var context = this.database.CreateContext(tenant.Runtime());

        var parentUnit = new OrganizationUnit { ParentId = rootId, Name = "Parent unit", UnitType = OrganizationUnitType.Region };
        var childUnit = new OrganizationUnit { Parent = parentUnit, Name = "Child unit", UnitType = OrganizationUnitType.Branch };
        var role = new TenantRole { Name = "Viewer", Permissions = [new TenantRolePermission { Permission = RolePermission.UsersView }] };
        context.AddRange(parentUnit, childUnit, role);
        context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { UserId = tenant.OwnerId, TenantRole = role, OrganizationUnitId = rootId });
        context.TenantSettings.Add(new TenantSetting { Key = TenantSettingDefinitions.TimeZone.Key, Value = "Europe/Stockholm" });

        var resource = new Resource { UserId = tenant.OwnerId, Memberships = [new ResourceUnitMembership { OrganizationUnitId = rootId }] };
        context.Resources.Add(resource);

        var qualification = new Qualification { Name = "First aid" };
        context.ResourceQualifications.Add(new ResourceQualification { Resource = resource, Qualification = qualification });

        context.Services.Add(new Staffora.Core.Entities.Catalogue.Service { Name = "Office cleaning", CrossTenantId = Guid.NewGuid(), Qualifications = [new Staffora.Core.Entities.Catalogue.ServiceQualification { Qualification = qualification }] });
        var pattern = new WorkPattern { Name = "Weekly", Segments = [new WorkPatternSegment { DayIndex = 0, Start = new TimeOnly(8, 0), End = new TimeOnly(16, 0) }] };
        context.WorkPatterns.Add(pattern);

        context.ResourcePatternAssignments.Add(new ResourcePatternAssignment { Resource = resource, WorkPattern = pattern, EffectiveFrom = new DateOnly(2026, 11, 9), CycleAnchor = new DateOnly(2026, 11, 9) });

        context.ResourceAbsences.Add(new ResourceAbsence { Resource = resource, Kind = AbsenceKind.SickLeave, FirstDay = new DateOnly(2026, 11, 9), LastDay = new DateOnly(2026, 11, 9) });

        context.ClosedDays.Add(new ClosedDay { Name = "Founders' Day", FirstDay = new DateOnly(2026, 12, 4), LastDay = new DateOnly(2026, 12, 4), OrganizationUnit = childUnit });

        var contact = new CustomerContact { Customer = new Customer { Name = "Brightpath Learning" }, FirstName = "Ada", LastName = "Moreno" };
        context.CustomerContacts.Add(contact);

        var job = new Job
        {
            Name = "Algebra",
            Customer = contact.Customer,
            Contact = contact,
            OrganizationUnitId = rootId,
            PeriodStart = new DateOnly(2027, 1, 11),
            PeriodEnd = new DateOnly(2027, 5, 28),
            TimeSlots = [new JobTimeSlot { Weekday = Weekday.Monday, Start = new TimeOnly(8, 0), End = new TimeOnly(9, 0) }],
        };
        context.Jobs.Add(job);
        var slot = new JobSlot { Job = job, Name = "Lead", State = JobSlotState.Create, Qualifications = [new JobSlotQualification { Qualification = qualification }] };
        context.JobSlots.Add(slot);

        var round = new RequestRound { Slot = slot };
        context.RequestRounds.Add(round);
        context.SlotAssignments.Add(new SlotAssignment { Slot = slot, Resource = resource, Round = round });

        var parent = new TestParent { Name = "Parent" };
        var child = new TestChild { Name = "Child", Parent = parent };
        child.Grandchildren.Add(new TestGrandchild { Name = "Grandchild" });
        parent.Children.Add(child);
        parent.Lines.Add(new TestParentLine { Code = "L1", Text = "Line" });
        context.TestParents.Add(parent);
        context.TestFolders.Add(new TestFolder { Name = "Folder", Documents = [new TestDocument { Name = "Document" }] });
        context.StateHistoryEntries.Add(new StateHistoryEntry
        {
            EntityType = nameof(TestParent),
            EntityId = parent.Id,
            TransitionKey = "test-parent-save",
            CorrelationId = parent.Id,
            ActorId = tenant.OwnerId,
        });
        context.Comments.Add(new Comment
        {
            CommentContainer = new CommentContainer { EntityType = nameof(TestParent), EntityId = parent.Id, Audience = CommentAudience.Internal },
            Text = "A note",
        });
        var outgoing = new OutgoingMessage { MessageType = TestParent.ActivatedMessage, EntityType = nameof(TestParent), EntityId = parent.Id, Subject = "Notice", Text = "A notice", RecipientUserIds = [tenant.OwnerId] };
        context.OutgoingMessages.Add(outgoing);
        context.StoredMessages.Add(new StoredMessage
        {
            OutgoingMessage = outgoing,
            MessageType = outgoing.MessageType,
            EntityType = outgoing.EntityType,
            EntityId = parent.Id,
            Subject = outgoing.Subject,
            Text = outgoing.Text,
            Recipients = [new MessageRecipient { UserId = tenant.OwnerId }],
        });

        var supplier = new Staffora.Core.Entities.Portal.Supplier { Name = "Supplying company", LinkedTenantId = otherTenant };
        context.Suppliers.Add(supplier);
        var request = new Staffora.Core.Entities.Portal.Order { Name = "Requested job", PeriodStart = new DateOnly(2027, 2, 1), PeriodEnd = new DateOnly(2027, 5, 31), Supplier = supplier, OrdererId = tenant.OwnerId };
        context.Orders.Add(request);

        context.OrderStaff.Add(new Staffora.Core.Entities.Portal.OrderStaff { Order = request, Name = "Ada Moreno" });

        var copy = new Staffora.Core.Entities.Portal.OfferedService { Supplier = supplier, LinkedCrossTenantId = Guid.NewGuid(), Name = "Office cleaning", Offered = true };
        context.OfferedServices.Add(copy);
        context.OrderLines.Add(new Staffora.Core.Entities.Portal.OrderLine { Order = request, OfferedService = copy, Count = 2 });

        context.IntegrationMessages.Add(new IntegrationMessage { Kind = "test.kind", TargetTenantId = otherTenant, CorrelationId = parent.Id, Payload = "{}" });
        context.IntegrationReceipts.Add(new IntegrationReceipt { MessageId = Guid.NewGuid(), Kind = "test.kind" });

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<List<Guid>> TenantIdsAsync(TestDataContext context, Type entityType, CancellationToken cancellationToken) =>
        await (Task<List<Guid>>)typeof(ReadSweepTests)
            .GetMethod(nameof(TenantIdsOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(entityType)
            .Invoke(null, [context, cancellationToken])!;

    private static Task<List<Guid>> TenantIdsOfAsync<TEntity>(TestDataContext context, CancellationToken cancellationToken) where TEntity : class, ITenantEntity =>
        context.Set<TEntity>().Select(row => row.TenantId).ToListAsync(cancellationToken);
}
