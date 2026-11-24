using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Data;
using Staffora.Services.Catalogue;
using Staffora.Services.Comments;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Customers;
using Staffora.Services.Diagnostics;
using Staffora.Services.Integration;
using Staffora.Services.Messaging;
using Staffora.Services.Portal;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;

namespace Staffora.Services.Workflow;

public static class WorkflowServiceRegistration
{
    public static IServiceCollection AddWorkflowServices(this IServiceCollection services)
    {
        services.TryAddSingleton<NoticeBus>();
        services.AddScoped<MessageChannelRouter>();
        services.AddScoped<IChannelSender, InAppChannelSender>();
        services.AddScoped<IOutgoingMessageDispatcher, OutgoingMessageDispatcher>();
        services.AddScoped<NoticeService>();
        services.AddScoped<IHandledNotices, HandledNotices>();

        services.TryAddSingleton<TenantChangeBus>();
        services.AddStafforaTelemetry();
        services.AddScoped<TenantChangeFeed>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<StateHistoryService>();

        services.AddScoped<UserAccountGuard>();
        services.AddScoped<IUserAccountGuard>(provider => provider.GetRequiredService<UserAccountGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<UserAccountGuard>());

        services.AddScoped<OrganizationUnitGuard>();
        services.AddScoped<IOrganizationUnitGuard>(provider => provider.GetRequiredService<OrganizationUnitGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<OrganizationUnitGuard>());
        services.AddScoped<TenantRoleGuard>();
        services.AddScoped<ITenantRoleGuard>(provider => provider.GetRequiredService<TenantRoleGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<TenantRoleGuard>());

        services.AddScoped<TenantOwnerGuard>();
        services.AddScoped<ITenantOwnerGuard>(provider => provider.GetRequiredService<TenantOwnerGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<TenantOwnerGuard>());

        services.AddScoped<ITenantSignOut, TenantSignOut>();

        services.AddScoped<IStepLock, Staffora.Services.Tenancy.TenantCreateStepLock>();

        services.AddStepLock<OrganizationUnit>(AppLock.Structure);
        services.AddStepLock<TenantRole>(AppLock.Structure);

        services.AddStepLock<ApplicationUser>(AppLock.Structure);
        services.AddScoped<IDeleteBlocker, RoleHoldingsBlockUnitDelete>();
        services.AddScoped<IDeleteBlocker, RoleHoldersBlockRoleDelete>();

        services.AddScoped<ResourceLinker>();
        services.AddScoped<IStepFollowUp, UserStepsKeepStaffMembers>();
        services.AddScoped<IStepFollowUp, RoleStepsKeepStaffMembers>();
        services.AddScoped<IStepFollowUp, UnitStepsKeepStaffMembers>();
        services.AddStepLock<Resource>(AppLock.Structure);
        services.AddScoped<IDeleteBlocker, ResourceMembersBlockUnitDelete>();
        services.AddScoped<IDeleteBlocker, ClosedDaysBlockUnitDelete>();

        services.AddScoped<PatternAssignmentGuard>();
        services.AddScoped<IPatternAssignmentGuard>(provider => provider.GetRequiredService<PatternAssignmentGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<PatternAssignmentGuard>());
        services.AddStepLock<ResourcePatternAssignment>(AppLock.Schedules);
        services.AddStepLock<WorkPattern>(AppLock.Schedules);
        services.AddScoped<IDeleteBlocker, PatternAssignmentsBlockPatternDelete>();

        services.AddScoped<ResourceAbsenceGuard>();
        services.AddScoped<IResourceAbsenceGuard>(provider => provider.GetRequiredService<ResourceAbsenceGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<ResourceAbsenceGuard>());

        services.AddStepLock<ResourceAbsence>(AppLock.Jobs);

        services.AddStepLock<Customer>(AppLock.Customers);
        services.AddStepLock<CustomerContact>(AppLock.Customers);
        services.AddScoped<IDeleteBlocker, CustomersBlockUnitDelete>();

        services.AddScoped<CustomerPortalProvisioner>();
        services.AddScoped<ICustomerPortalProvisioner>(provider => provider.GetRequiredService<CustomerPortalProvisioner>());
        services.AddScoped<IStepFollowUp, CustomerPortalFollowUp>();

        services.AddScoped<OrderSender>();
        services.AddScoped<IOrderSender>(provider => provider.GetRequiredService<OrderSender>());
        services.AddScoped<IStepFollowUp, OrderSendFollowUp>();

        services.AddScoped<IJobStateSync, JobStateSync>();
        services.AddIntegrationHandler<JobStateSyncHandler>(OrderSync.JobStateKind);
        services.AddIntegrationHandler<OrderCancelHandler>(OrderSync.CancelKind);

        services.AddIntegrationHandler<JobCommentHandler>(JobThreadSync.ToPortalKind);
        services.AddIntegrationHandler<OrderCommentHandler>(JobThreadSync.ToSupplierKind);
        services.AddScoped<IDeleteBlocker, CommentsBlockContactDelete>();

        services.AddScoped<IStepFollowUp, PortalUserRoleFollowUp>();

        services.AddScoped<JobGuard>();
        services.AddScoped<IJobGuard>(provider => provider.GetRequiredService<JobGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<JobGuard>());
        services.AddStepLock<Job>(AppLock.Structure, AppLock.Customers, AppLock.Jobs);
        services.AddStepLock<JobSlot>(AppLock.Jobs);

        services.AddScoped<JobSlotGuard>();
        services.AddScoped<IJobSlotGuard>(provider => provider.GetRequiredService<JobSlotGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<JobSlotGuard>());
        services.AddStepLock<Qualification>(AppLock.Jobs);
        services.AddScoped<IDeleteBlocker, JobsBlockCustomerDelete>();

        services.AddStepLock<SlotAssignment>(AppLock.Jobs);
        services.AddScoped<IDeleteBlocker, AssignmentsBlockSlotDelete>();

        services.AddScoped<IStepFollowUp, SlotGroupFollowUp>();

        services.AddScoped<IStepFollowUp, SlotStepsSettleTheirGroups>();

        services.AddStepLock<RequestRound>(AppLock.Jobs);
        services.AddScoped<IDeleteBlocker, RoundsBlockSlotDelete>();

        services.AddScoped<SlotAssignmentGuard>();
        services.AddScoped<ISlotAssignmentGuard>(provider => provider.GetRequiredService<SlotAssignmentGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<SlotAssignmentGuard>());
        services.AddScoped<IDeleteBlocker, JobsBlockContactDelete>();
        services.AddScoped<IDeleteBlocker, JobsBlockUnitDelete>();

        services.AddScoped<ServiceGuard>();
        services.AddScoped<IServiceGuard>(provider => provider.GetRequiredService<ServiceGuard>());
        services.AddScoped<IStepPreparation>(provider => provider.GetRequiredService<ServiceGuard>());
        services.AddStepLock<Service>(AppLock.Customers, AppLock.Jobs);

        services.AddScoped<IStepFollowUp, ServiceOfferFollowUp>();
        services.AddIntegrationHandler<ServiceOfferedHandler>(ServiceOfferSync.OfferedKind);
        services.AddIntegrationHandler<ServiceChangedHandler>(ServiceOfferSync.ChangedKind);
        services.AddIntegrationHandler<ServiceWithdrawnHandler>(ServiceOfferSync.WithdrawnKind);

        services.AddScoped<CommentService>();
        services.AddScoped<IDeleteBlocker, CommentThreadsBlockDelete>();
        return services;
    }
}
