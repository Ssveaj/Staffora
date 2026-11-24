using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Services;

public class UserService
{
    public static readonly IReadOnlyList<RolePermission> Viewers = [RolePermission.UsersView, RolePermission.CustomerUsersManage];

    public static readonly IReadOnlyList<RolePermission> Creators = [RolePermission.UsersCreate, RolePermission.CustomerUsersManage];

    public static readonly IReadOnlyList<RolePermission> Editors = [RolePermission.UsersEdit, RolePermission.CustomerUsersManage];

    private const string NoViewPermission = "You do not have permission to view users.";
    private const string NoEditPermission = "You do not have permission to edit users.";
    private const string NoCreatePermission = "You do not have permission to create users.";
    private const string NoResourcesViewPermission = "You do not have permission to view staff members.";
    private const string SystemAccountNameRefused = "A user name starting with \"" + SystemAccount.UserNamePrefix + "\" is reserved for a tenant's system account.";

    public const string UserNotFound = "User not found.";

    public const string UserReadFailed = "The user could not be read. Try again.";

    public const string CreateInOwnTenantOnly = "You are not authorized to create users in this tenant.";

    public const string SignInNameUnavailable = "That user name or email address cannot be used.";

    private static readonly HashSet<string> takenCodes = [nameof(IdentityErrorDescriber.DuplicateUserName), nameof(IdentityErrorDescriber.DuplicateEmail)];

    private static DetachedEntityUpdaterOptions<ApplicationUser> UpdaterOptions => new DetachedEntityUpdaterOptions<ApplicationUser>()
        .WithCollection(user => user.Attributes)
        .Excluding(user => user.ConcurrencyStamp, () => Guid.NewGuid().ToString())
        .Excluding(user => user.PasswordHash)
        .Excluding(user => user.SecurityStamp)
        .Excluding(user => user.EmailConfirmed)
        .Excluding(user => user.PhoneNumberConfirmed)
        .Excluding(user => user.TwoFactorEnabled)
        .Excluding(user => user.LockoutEnd)
        .Excluding(user => user.LockoutEnabled)
        .Excluding(user => user.AccessFailedCount)
        .Excluding(user => user.LastSignedIn);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly CustomFieldService attributes;
    private readonly RoleHoldingService holdings;
    private readonly UserPasswordIssuer passwords;
    private readonly ResourceService resources;
    private readonly QualificationService qualifications;
    private readonly PatternAssignmentService patternAssignments;
    private readonly StaffAvailabilityService availability;
    private readonly ResourceAbsenceService absences;
    private readonly TenantClock clock;
    private readonly TenantRoleService roles;
    private readonly ILogger<UserService> log;

    public UserService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, UserManager<ApplicationUser> userManager, PermissionService permissionService, WorkflowService workflows, StateHistoryService history, CustomFieldService attributes, RoleHoldingService holdings, UserPasswordIssuer passwords, ResourceService resources, QualificationService qualifications, PatternAssignmentService patternAssignments, StaffAvailabilityService availability, ResourceAbsenceService absences, TenantClock clock, TenantRoleService roles, ILogger<UserService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.userManager = userManager;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.attributes = attributes;
        this.holdings = holdings;
        this.passwords = passwords;
        this.resources = resources;
        this.qualifications = qualifications;
        this.patternAssignments = patternAssignments;
        this.availability = availability;
        this.absences = absences;
        this.clock = clock;
        this.roles = roles;
        this.log = log;
    }

    public async Task<Result<ListPage<UserListItemDto>>> QueryUsersAsync(ListQuery query)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Viewers))
            return Result<ListPage<UserListItemDto>>.Failure(NoViewPermission);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<UserListItemDto>>.Failure(IRuntimeExecutionContext.NoTenant);

        var readRoles = await this.permissionService.HasAnyPermissionAsync([RolePermission.RolesView, RolePermission.CustomerUsersManage]);
        return await QueryUsersOfAsync(tenantId, query, readRoles, platformReason: null);
    }

    internal Task<Result<ListPage<UserListItemDto>>> QueryConsoleUsersAsync(Guid tenantId, ListQuery query) =>
        QueryUsersOfAsync(tenantId, query, readRoles: true, platformReason: "list a tenant's users on its console");

    private async Task<Result<ListPage<UserListItemDto>>> QueryUsersOfAsync(Guid tenantId, ListQuery query, bool readRoles, string? platformReason)
    {
        var list = UserList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = platformReason is null ? null : context.BeginPlatformScope(this.runtime, platformReason);
            if (platformReason is not null && lease is null)
            {
                return Result<ListPage<UserListItemDto>>.Failure("Only the platform console lists another tenant's users.");
            }

            var users = UsersInScope(context).AsNoTracking().Where(user => user.TenantId == tenantId);
            if (list.Search is { Length: > 0 } search)
            {
                users = users.Where(user => (user.FirstName + " " + user.LastName).Contains(search)
                    || (user.Email != null && user.Email.Contains(search))
                    || (user.UserName != null && user.UserName.Contains(search)));
            }

            users = list.Filter(UserList.Status) switch
            {
                UserList.Active => users.Where(user => user.State == UserState.Active),
                UserList.Inactive => users.Where(user => user.State != UserState.Active),
                _ => users,
            };

            var total = await users.CountAsync();
            var page = ListPage<UserListItemDto>.PageWithin(list, total);
            var order = new ListOrder<ApplicationUser>(users);
            var grouped = list.Group == UserList.ByStatus;
            if (grouped)
            {
                order.By(user => user.State == UserState.Active ? 0 : 1);
            }

            _ = list.Sort switch
            {
                UserList.SortEmail => order.By(user => user.Email, list.Descending),
                UserList.SortUserName => order.By(user => user.UserName, list.Descending),
                _ => order.By(user => user.FirstName, list.Descending).By(user => user.LastName, list.Descending).By(user => user.Email, list.Descending),
            };

            var rows = await order.By(user => user.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(user => new UserListItemDto
                {
                    Id = user.Id,
                    DisplayName = string.IsNullOrWhiteSpace((user.FirstName + " " + user.LastName).Trim())
                        ? (user.Email ?? user.UserName ?? "Unknown user") : (user.FirstName + " " + user.LastName).Trim(),
                    Email = user.Email ?? string.Empty,
                    UserName = user.UserName ?? string.Empty,
                    PhoneNumber = user.PhoneNumber,
                    State = user.State,
                    Created = user.Created,
                    LastSignedIn = user.LastSignedIn,
                })
                .ToListAsync();

            if (readRoles && rows.Count > 0)
            {
                var ids = rows.Select(row => row.Id).ToList();
                var holdings = await context.OrganizationUnitUserRoles.AsNoTracking()
                    .Where(holding => holding.TenantId == tenantId && ids.Contains(holding.UserId))
                    .Select(holding => new { holding.UserId, Role = holding.TenantRole.Name, Unit = holding.OrganizationUnit.Name })
                    .ToListAsync();
                var byUser = holdings.ToLookup(holding => holding.UserId);
                foreach (var row in rows)
                {
                    row.Roles = [.. byUser[row.Id].OrderBy(holding => holding.Role, StringComparer.CurrentCultureIgnoreCase).ThenBy(holding => holding.Unit, StringComparer.CurrentCultureIgnoreCase).Select(holding => $"{holding.Role} on {holding.Unit}")];
                    row.RoleNames = [.. byUser[row.Id].Select(holding => holding.Role).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
                }
            }

            if (!grouped)
            {
                return Result<ListPage<UserListItemDto>>.Success(ListPage<UserListItemDto>.Ungrouped(rows, total, page, list.PageSize));
            }

            var counts = await users.GroupBy(user => user.State == UserState.Active).Select(group => new { group.Key, Count = group.Count() }).ToListAsync();
            var groups = counts.ToDictionary(
                count => count.Key ? UserList.Active : UserList.Inactive,
                count => (count.Key ? "Active" : "Inactive", count.Count));
            return Result<ListPage<UserListItemDto>>.Success(ListPage<UserListItemDto>.Grouped(rows, total, page, list.PageSize, row => row.State == UserState.Active ? UserList.Active : UserList.Inactive, groups));
        }
        catch (Exception)
        {
            return Result<ListPage<UserListItemDto>>.Failure("An error occurred while fetching users.");
        }
    }

    public async Task<Result<ApplicationUser>> GetUserByIdAsync(Guid userId)
    {
        if (userId != this.runtime.EffectiveUserId && !await this.permissionService.HasAnyPermissionAsync(Viewers))
            return Result<ApplicationUser>.Failure(NoViewPermission);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "read a user of any tenant");

            var user = await UsersInScope(context)
                .Include(u => u.Attributes)
                .FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null)
                return Result<ApplicationUser>.Failure(UserNotFound);

            return Result<ApplicationUser>.Success(user);
        }
        catch (Exception)
        {
            return Result<ApplicationUser>.Failure("An error occurred while fetching the user.");
        }
    }

    public async Task<Result<ApplicationUser>> CreateUserAsync(ApplicationUser user, Guid tenantId, string password, string? transitionKey = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!await this.permissionService.HasAnyPermissionAsync(Creators))
            return Result<ApplicationUser>.Failure(NoCreatePermission);

        if (this.runtime.TenantId != tenantId)
            return Result<ApplicationUser>.Failure(CreateInOwnTenantOnly);

        var created = await CreateCoreAsync(user, password, transitionKey ?? ApplicationUserWorkflowAttribute.CreateStep, CancellationToken.None);
        return created.IsSuccess ? Result<ApplicationUser>.Success(created.Value!) : Result<ApplicationUser>.Failure(created.Error);
    }

    public async Task<Result<Guid>> CreateApplicationUserAsync(ApplicationUser user, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!await this.permissionService.HasAnyPermissionAsync(Creators, cancellationToken))
            return Result<Guid>.Failure(NoCreatePermission);

        var password = UserPasswords.Generate();
        var created = await CreateCoreAsync(user, password, transitionKey, cancellationToken);
        return created.IsSuccess
            ? Result<Guid>.Success(created.Value!.Id, $"The user is created. Their initial password is {password} — give it to them now: it is shown only this once.")
            : Result<Guid>.Failure(created.Error);
    }

    private async Task<Result<ApplicationUser>> CreateCoreAsync(ApplicationUser source, string password, string transitionKey, CancellationToken cancellationToken)
    {
        if (this.runtime.TenantId is not { } tenantId)
            return Result<ApplicationUser>.Failure(IRuntimeExecutionContext.NoTenant);

        try
        {
            var built = await NewUserRowAsync(source, tenantId, password);
            if (!built.IsSuccess || built.Value is not { } row)
                return built;

            var values = CustomFieldValues.Normalized(source.Attributes).Where(value => !AttributeValues.IsEmpty(value)).ToList();
            if (values.Count > 0)
            {
                await using var check = this.dbFactory.CreateDbContext(this.runtime);
                if (await CustomFieldValues.DescribeInvalidAsync<ApplicationUserAttributeDefinition, ApplicationUserAttribute>(check, tenantId, values, cancellationToken: cancellationToken) is { } invalid)
                    return Result<ApplicationUser>.Failure(invalid);

                foreach (var value in values)
                {
                    row.Attributes.Add(NewValue(row.Id, value));
                }
            }

            var created = await this.workflows.CreateAndTransitionAsync<UserState, ApplicationUser>(row, transitionKey, ApplicationUserWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
            if (created.IsSuccess)
                return Result<ApplicationUser>.Success(row);

            return await SignInNameTakenAsync(row) ? Result<ApplicationUser>.Failure(SignInNameUnavailable) : Result<ApplicationUser>.Failure(created.Error);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<ApplicationUser>.Failure("An error occurred while creating the user.");
        }
    }

    internal async Task<Result<ApplicationUser>> NewUserRowAsync(ApplicationUser source, Guid tenantId, string password)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsSystemAccountName(source.UserName))
            return Result<ApplicationUser>.Failure(SystemAccountNameRefused);

        if (string.IsNullOrWhiteSpace(password))
            return Result<ApplicationUser>.Failure("A password is required.");

        var row = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FirstName = (source.FirstName ?? string.Empty).Trim(),
            LastName = (source.LastName ?? string.Empty).Trim(),
            UserName = source.UserName?.Trim(),
            Email = source.Email?.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(source.PhoneNumber) ? null : source.PhoneNumber.Trim(),
            TenantId = tenantId,
            State = UserState.Create,
            EmailConfirmed = true,
            LockoutEnabled = this.userManager.Options.Lockout.AllowedForNewUsers,
            SecurityStamp = UserPasswords.NewSecurityStamp(),
        };
        row.NormalizedUserName = this.userManager.NormalizeName(row.UserName);
        row.NormalizedEmail = this.userManager.NormalizeEmail(row.Email);

        if (!string.IsNullOrWhiteSpace(row.UserName) && !string.IsNullOrWhiteSpace(row.Email) && await IdentityRefusalAsync(row) is { } refusal)
            return Result<ApplicationUser>.Failure(refusal);

        foreach (var validator in this.userManager.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(this.userManager, row, password);
            if (!validation.Succeeded)
                return Result<ApplicationUser>.Failure(Describe(validation.Errors));
        }

        row.PasswordHash = this.userManager.PasswordHasher.HashPassword(row, password);
        return Result<ApplicationUser>.Success(row);
    }

    internal Task<bool> SignInNameHeldByAnotherAsync(ApplicationUser user) => SignInNameTakenAsync(user);

    public async Task<Result<DetachedEntityUpdater<ApplicationUser>>> LoadApplicationUserUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await MayOpenAsync(id, cancellationToken))
            return Result<DetachedEntityUpdater<ApplicationUser>>.Failure(NoViewPermission);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "open a user's dialog");
            var user = await context.Users
                .WithWorkflowGraph()
                .ExceptSystemAccounts()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return user is null
                ? Result<DetachedEntityUpdater<ApplicationUser>>.Failure(UserNotFound)
                : Result<DetachedEntityUpdater<ApplicationUser>>.Success(new DetachedEntityUpdater<ApplicationUser>(user, context, UpdaterOptions));
        }, this.log, $"the user {id}", UserReadFailed, cancellationToken);
    }

    public async Task<Result<ApplicationUser>> NewApplicationUserAsync(CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Creators, cancellationToken))
            return Result<ApplicationUser>.Failure(NoCreatePermission);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.HasPlatformReach())
            return Result<ApplicationUser>.Failure(CreateInOwnTenantOnly);

        return Result<ApplicationUser>.Success(new ApplicationUser { TenantId = tenantId, State = UserState.Create });
    }

    public async Task<Result> UpdateApplicationUserAndTransitionAsync(DetachedEntityUpdater<ApplicationUser> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasAnyPermissionAsync(Editors, cancellationToken))
            return Result.Failure(NoEditPermission);

        var id = (Guid)updater.OriginalValue(nameof(ApplicationUser.Id))!;
        var tenantId = (Guid)updater.OriginalValue(nameof(ApplicationUser.TenantId))!;
        var user = updater.Entity;
        try
        {
            var signInChanged = false;
            if (!TakesNoEdits(transitionKey))
            {
                user.UserName = user.UserName?.Trim();
                user.Email = user.Email?.Trim();
                signInChanged = !string.Equals(user.UserName, (string?)updater.OriginalValue(nameof(ApplicationUser.UserName)), StringComparison.Ordinal)
                    || !string.Equals(user.Email, (string?)updater.OriginalValue(nameof(ApplicationUser.Email)), StringComparison.Ordinal);
                if (signInChanged && IsSystemAccountName(user.UserName))
                    return Result.Failure(SystemAccountNameRefused);

                user.NormalizedUserName = this.userManager.NormalizeName(user.UserName);
                user.NormalizedEmail = this.userManager.NormalizeEmail(user.Email);
                if (signInChanged && !string.IsNullOrWhiteSpace(user.UserName) && !string.IsNullOrWhiteSpace(user.Email) && await IdentityRefusalAsync(user) is { } refusal)
                    return Result.Failure(refusal);

                foreach (var emptied in CustomFieldValues.Normalized(user.Attributes).Where(value => AttributeValues.IsEmpty(value)).ToList())
                {
                    user.Attributes.Remove(emptied);
                }

                if (await InvalidCustomFieldsAsync(id, tenantId, user.Attributes, cancellationToken) is { } invalid)
                    return Result.Failure(invalid);

                if (this.runtime.Current is var session && tenantId != session.TenantId && session.HasPlatformReach()
                    && updater.GetChangedMembers().Any(member => !ConsoleEditableColumns.Contains(member)))
                    return Result.Failure(ConsoleEditsIdentityOnly);
            }

            Result saved;
            string? issued;
            try
            {
                saved = await this.workflows.SaveAndTransitionAsync<UserState, ApplicationUser>(updater, transitionKey, ApplicationUserWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            }
            finally
            {
                issued = this.passwords.TakeIssued(id);
            }

            if (!saved.IsSuccess)
            {
                return signInChanged && await SignInNameTakenAsync(user) ? Result.Failure(SignInNameUnavailable) : saved;
            }

            return issued is null
                ? Result.Success()
                : Result.Success(message: $"The new password is {issued} — give it to the user now: it is shown only this once.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result.Failure("An error occurred while saving the user.");
        }
    }

    public static IReadOnlySet<string> ConsoleEditableColumns { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ApplicationUser.FirstName),
        nameof(ApplicationUser.LastName),
        nameof(ApplicationUser.Email),
        nameof(ApplicationUser.NormalizedEmail),
        nameof(ApplicationUser.UserName),
        nameof(ApplicationUser.NormalizedUserName),
        nameof(ApplicationUser.ConcurrencyStamp),
    };

    public const string ConsoleEditsIdentityOnly = "From the platform console only a user's name, email address and user name can be changed.";

    public async Task<Result<StateHistoryPage>> GetApplicationUserHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await MayOpenAsync(id, cancellationToken)
            ? await this.history.GetHistoryAsync<ApplicationUser>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoViewPermission);

    public async Task<Result<IReadOnlyList<IEntityAttributeDefinition>>> GetApplicationUserCustomFieldsAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (!(userId is { } id ? await MayOpenAsync(id, cancellationToken) : await this.permissionService.HasAnyPermissionAsync(Creators, cancellationToken)))
            return Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(userId is null ? NoCreatePermission : NoViewPermission);

        if (await TenantOfAsync(userId, cancellationToken) is not { } tenantId)
            return Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(UserNotFound);

        if (await this.permissionService.GetEditionAsync(cancellationToken) == TenantEdition.CustomerPortal)
            return Result<IReadOnlyList<IEntityAttributeDefinition>>.Success([]);

        var fields = await this.attributes.GetDefinitionsAsync<ApplicationUserAttributeDefinition>(tenantId);
        return fields.IsSuccess && fields.Value is { } read
            ? Result<IReadOnlyList<IEntityAttributeDefinition>>.Success([.. read])
            : Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(fields.Error);
    }

    public async Task<Result<UserEditorOptions>> GetApplicationUserEditorOptionsAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (!(userId is { } id ? await MayOpenAsync(id, cancellationToken) : await this.permissionService.HasAnyPermissionAsync(Creators, cancellationToken)))
            return Result<UserEditorOptions>.Failure(userId is null ? NoCreatePermission : NoViewPermission);

        if (await TenantOfAsync(userId, cancellationToken) is not { } tenantId)
            return Result<UserEditorOptions>.Failure(UserNotFound);

        var session = this.runtime.Current;
        var onConsole = userId is not null && tenantId != session.TenantId && session.HasPlatformReach();
        var mayEdit = userId is null || ((tenantId == session.TenantId || onConsole) && await this.permissionService.HasPermissionAsync(RolePermission.UsersEdit, cancellationToken));
        var choices = await this.holdings.GetRoleHoldingChoicesAsync(tenantId, cancellationToken);
        StaffSummary? staff = null;
        if (userId is { } user && await this.permissionService.HasPermissionAsync(RolePermission.ResourcesView, cancellationToken))
        {
            var summary = await this.resources.SummaryOfAsync(user, cancellationToken);
            if (!summary.IsSuccess)
                return Result<UserEditorOptions>.Failure(summary.Error);

            staff = summary.Value;
        }

        var manageQualifications = staff is not null && userId is { } staffUser && await this.qualifications.MayManageQualificationsOfAsync(staffUser, cancellationToken);

        StaffSchedule? schedule = null;
        string? scheduleProblem = null;
        var assignPatterns = false;
        if (staff is not null && userId is { } scheduled)
        {
            var today = await this.clock.TodayAsync(cancellationToken);
            var read = today.IsSuccess
                ? await this.availability.ReadScheduleOfUserAsync(scheduled, today.Value, StaffSchedule.PeriodWeeks, cancellationToken)
                : Result<StaffSchedule>.Failure(today.Error);
            schedule = read.IsSuccess ? read.Value : null;
            scheduleProblem = read.IsSuccess ? null : read.Error;
            assignPatterns = await this.patternAssignments.MayAssignToUserAsync(scheduled, cancellationToken);
        }

        var addAbsence = staff is not null && userId is { } absent && await this.absences.MayAddAbsenceForUserAsync(absent, cancellationToken);

        return Result<UserEditorOptions>.Success((choices.IsSuccess && choices.Value is { } offered
            ? new UserEditorOptions(!mayEdit, true, offered)
            : new UserEditorOptions(!mayEdit, false, RoleHoldingChoices.None)) with
        { Staff = staff, CanManageQualifications = manageQualifications, Schedule = schedule, ScheduleProblem = scheduleProblem, CanAssignPatterns = assignPatterns, CanAddAbsence = addAbsence, OnConsole = onConsole });
    }

    public RoleHoldingService Holdings => this.holdings;

    public TenantRoleService Roles => this.roles;

    public PatternAssignmentService PatternAssignments => this.patternAssignments;

    public ResourceAbsenceService Absences => this.absences;

    public async Task<Result<ListPage<StaffQualificationRow>>> QueryApplicationUserQualificationsAsync(Guid userId, ListQuery query, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ResourcesView, cancellationToken))
            return Result<ListPage<StaffQualificationRow>>.Failure(NoResourcesViewPermission);

        return await this.qualifications.QualificationsOfAsync(userId, query, cancellationToken);
    }

    public async Task<Result<ListPage<StaffUnitRow>>> QueryApplicationUserStaffUnitsAsync(Guid userId, ListQuery query, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ResourcesView, cancellationToken))
            return Result<ListPage<StaffUnitRow>>.Failure(NoResourcesViewPermission);

        return await this.resources.UnitsOfAsync(userId, query, cancellationToken);
    }

    private static IQueryable<ApplicationUser> UsersInScope(ApplicationDbContext context) => context.Users.ExceptSystemAccounts();

    private async Task<bool> MayOpenAsync(Guid id, CancellationToken cancellationToken) =>
        id == this.runtime.EffectiveUserId || await this.permissionService.HasAnyPermissionAsync(Viewers, cancellationToken);

    private async Task<Guid?> TenantOfAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is not { } id)
            return this.runtime.TenantId;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "read the tenant of a user of any tenant");
        return await UsersInScope(context).AsNoTracking().Where(user => user.Id == id).Select(user => (Guid?)user.TenantId).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<string?> IdentityRefusalAsync(ApplicationUser user)
    {
        foreach (var validator in this.userManager.UserValidators)
        {
            var validation = await validator.ValidateAsync(this.userManager, user);
            if (!validation.Succeeded)
                return Describe(validation.Errors);
        }

        return null;
    }

    private async Task<bool> SignInNameTakenAsync(ApplicationUser user)
    {
        var byName = string.IsNullOrWhiteSpace(user.UserName) ? null : await this.userManager.FindByNameAsync(user.UserName);
        var byEmail = string.IsNullOrWhiteSpace(user.Email) ? null : await this.userManager.FindByEmailAsync(user.Email);
        return (byName is not null && byName.Id != user.Id) || (byEmail is not null && byEmail.Id != user.Id);
    }

    private async Task<string?> InvalidCustomFieldsAsync(Guid userId, Guid tenantId, IEnumerable<ApplicationUserAttribute> values, CancellationToken cancellationToken)
    {
        var edited = CustomFieldValues.Normalized(values);
        if (edited.Count == 0)
            return null;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "judge the custom fields of a user of any tenant");
        var stored = await context.ApplicationUserAttributes.AsNoTracking().Where(value => value.EntityId == userId).ToListAsync(cancellationToken);
        return await CustomFieldValues.DescribeInvalidAsync<ApplicationUserAttributeDefinition, ApplicationUserAttribute>(context, tenantId, edited, stored, cancellationToken);
    }

    private static string Describe(IEnumerable<IdentityError> errors) =>
        string.Join(" ", errors.Select(error => takenCodes.Contains(error.Code) ? SignInNameUnavailable : error.Description).Distinct());

    private static bool IsSystemAccountName(string? userName) =>
        userName?.Trim().StartsWith(SystemAccount.UserNamePrefix, StringComparison.OrdinalIgnoreCase) == true;

    private static bool TakesNoEdits(string transitionKey) =>
        Staffora.Core.Workflow.WorkflowRegistry.Default.Get<UserState, ApplicationUser>().TryGetTransition(transitionKey, out var step) && step.TakesNoEdits;

    private static ApplicationUserAttribute NewValue(Guid userId, ApplicationUserAttribute value)
    {
        var row = new ApplicationUserAttribute { EntityId = userId, EntityAttributeDefinitionId = value.EntityAttributeDefinitionId };
        AttributeValues.CopyValues(value, row);
        return row;
    }
}
