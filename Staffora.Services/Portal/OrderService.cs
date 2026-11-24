using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Caching;
using Staffora.Services.Comments;
using Staffora.Services.Diagnostics;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;

namespace Staffora.Services.Portal;

public sealed class OrderService
{
    public static readonly IReadOnlyList<RolePermission> Readers = [RolePermission.OrdersView];

    public static readonly IReadOnlyList<RolePermission> Steppers = [RolePermission.OrdersCreate, RolePermission.OrdersSend, RolePermission.OrdersCancel];

    public const string NoView = "You do not have permission to view your organization's orders.";

    public const string NoCreate = "You do not have permission to write orders.";

    public const string NoStep = "You do not have permission to write, send or cancel orders.";

    public const string NotFound = "Order not found.";

    public const string NameMissing = "An order names the job it asks for.";

    public static readonly string PlaceTooLong = $"An order's place has at most {Job.PlaceNameMaxLength} characters in its name, {Job.StreetAddressMaxLength} in its street address, {Job.PostalCodeMaxLength} in its postal code and {Job.CityMaxLength} in its city.";

    public static readonly string TextTooLong = $"An order's job name has at most {Order.NameMaxLength} characters and its description {Order.DescriptionMaxLength}.";

    public const string CreateBySave = "An order is written by saving it as a draft. It is sent from the draft.";

    public const string NoSupplier = "No staffing company takes orders from your organization right now.";

    public const string InsidePortalOnly = "The platform console keeps no portal's orders: impersonate one of its people to see them.";

    public const string ReadFailed = "The orders could not be read. Try again.";

    public static readonly string LinesInvalid = $"Each line names one of the supplier's services and {OrderLine.MinCount} to {OrderLine.MaxCount} people. An order holds at most {OrderLine.MaxLines} lines.";

    public const string LineNotOffered = "A service no longer offered is not added to an order.";

    internal static readonly LookupCatalogue<OfferedServiceRow> OfferedCatalogue = new("offered services", LoadOfferedAsync);

    private static DetachedEntityUpdaterOptions<Order> UpdaterOptions => new DetachedEntityUpdaterOptions<Order>()
        .WithCollection(request => request.Lines);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly OrderSender sender;
    private readonly TenantClock clock;
    private readonly CommentService comments;
    private readonly TenantLookupCache lookupCache;
    private readonly ILogger<OrderService> log;

    public OrderService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, WorkflowService workflows, StateHistoryService history, OrderSender sender, TenantClock clock, CommentService comments, TenantLookupCache lookupCache, ILogger<OrderService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.sender = sender;
        this.clock = clock;
        this.comments = comments;
        this.lookupCache = lookupCache;
        this.log = log;
    }

    public async Task<Result<ListPage<OrderListItem>>> QueryRequestsAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (await RefusalAsync(Readers, NoView, cancellationToken) is { } refusal)
            return Result<ListPage<OrderListItem>>.Failure(refusal);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } userId)
            return Result<ListPage<OrderListItem>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = OrderList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var requests = context.Orders.AsNoTracking().Where(request => request.TenantId == tenantId);
            if (list.Search is { Length: > 0 } search)
            {
                requests = requests.Where(request => request.Name.Contains(search) || (request.Description != null && request.Description.Contains(search)));
            }

            if (list.Filter(OrderList.Status) is { } status && ListOption.TryParseEnum<OrderState>(status, out var state))
            {
                requests = requests.Where(request => request.State == state);
            }

            var hidden = new Dictionary<string, int>();
            if (!list.IsOn(OrderList.Everyone))
            {
                hidden[OrderList.Everyone] = await requests.CountAsync(request => request.OrdererId != userId, cancellationToken);
                requests = requests.Where(request => request.OrdererId == userId);
            }

            var total = await requests.CountAsync(cancellationToken);
            var page = ListPage<OrderListItem>.PageWithin(list, total);
            var order = new ListOrder<Order>(requests);
            order = list.Sort switch
            {
                OrderList.SortJob => order.By(request => request.Name, list.Descending),
                OrderList.SortPeriod => order.By(request => request.PeriodStart, list.Descending).By(request => request.Name),
                _ => order.By(request => request.Created, list.Descending),
            };
            var rows = await order.By(request => request.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(request => new OrderListItem(request.Id, request.Name, request.PeriodStart, request.PeriodEnd, request.State, request.Orderer.FirstName + " " + request.Orderer.LastName, request.OrdererId == userId, request.Created)
                {
                    Times = new JobTimes(request.Schedule, request.PeriodStart, request.PeriodEnd, request.StartTime, request.EndTime, new List<OccurrenceTimes>()),
                })
                .ToListAsync(cancellationToken);
            return Result<ListPage<OrderListItem>>.Success(ListPage<OrderListItem>.Ungrouped(rows, total, page, list.PageSize) with { HiddenCounts = hidden });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the orders of tenant {TenantId} failed.", tenantId);
            return Result<ListPage<OrderListItem>>.Failure(ReadFailed);
        }
    }

    public static readonly CommentThreadAccess RequestThread = new(CommentAudience.Customer, RolePermission.OrdersView, RolePermission.OrdersCreate);

    public const string RequestThreadTitle = "Messages with supplier";

    public const string RequestThreadClosed = "The order is completed, canceled or declined, so its messages take no new ones.";

    public const string RequestThreadNotSent = "Send the order first: its messages with the staffing company open once it is sent.";

    public async Task<Result<IReadOnlyList<CommentThread>>> GetOrderCommentsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(Readers, NoView, cancellationToken) is { } refusal)
            return Result<IReadOnlyList<CommentThread>>.Failure(refusal);

        RequestThreadFacts? facts;
        try
        {
            facts = await RequestThreadFactsAsync(id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the messages of the order {RequestId} failed.", id);
            return Result<IReadOnlyList<CommentThread>>.Failure(ReadFailed);
        }

        if (facts is null)
            return Result<IReadOnlyList<CommentThread>>.Failure(NotFound);

        if (!JobThreadSync.ShowsOnRequest(facts.State))
            return Result<IReadOnlyList<CommentThread>>.Success([]);

        var threads = await this.comments.GetThreadsAsync<Order>(id, [RequestAccess(facts)], cancellationToken);
        return threads.IsSuccess && threads.Value is { } read
            ? Result<IReadOnlyList<CommentThread>>.Success([.. read.Select(thread => thread.JudgedBySide() with { Title = RequestThreadTitle, Readers = $"Between your organization and {facts.Supplier}" })])
            : threads;
    }

    public async Task<Result<CommentThread>> AddOrderCommentAsync(Guid id, CommentAudience audience, string text, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(Readers, NoView, cancellationToken) is { } refusal)
            return Result<CommentThread>.Failure(refusal);

        if (audience != CommentAudience.Customer)
            return Result<CommentThread>.Failure(CommentService.NoAccess);

        RequestThreadFacts? facts;
        CommentWriter? writer;
        try
        {
            facts = await RequestThreadFactsAsync(id, cancellationToken);
            writer = await WriterAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the messages of the order {RequestId} failed.", id);
            return Result<CommentThread>.Failure(ReadFailed);
        }

        if (facts is null || writer is null)
            return Result<CommentThread>.Failure(NotFound);

        if (!JobThreadSync.ShowsOnRequest(facts.State) || facts.Correlation is not { } correlation)
            return Result<CommentThread>.Failure(RequestThreadNotSent);

        if (!string.IsNullOrWhiteSpace(text) && JobThreadSync.WriteToSupplier(new ToSupplierComment(Guid.NewGuid(), text.Trim(), writer)) is null)
            return Result<CommentThread>.Failure(JobThreadSync.TooLongToSend);

        var access = RequestAccess(facts) with
        {
            Effects = new CommentPostEffects(null, (context, comment, _) =>
            {
                var payload = JobThreadSync.WriteToSupplier(new ToSupplierComment(comment.CrossTenantId!.Value, comment.Text, writer))
                    ?? throw new InvalidOperationException("An order's message no longer fits its payload.");
                IntegrationOutbox.Enqueue(context, JobThreadSync.ToSupplierKind, facts.SupplierTenantId, correlation, payload);
                return Task.FromResult(CommentPostOutcome.None);
            })
            {
                Crosses = true,
            },
        };
        var posted = await this.comments.AddCommentAsync<Order>(id, access, text, cancellationToken);
        return posted.IsSuccess && posted.Value is { } thread
            ? Result<CommentThread>.Success(thread.JudgedBySide() with { Title = RequestThreadTitle, Readers = $"Between your organization and {facts.Supplier}" }, posted.Message)
            : posted;
    }

    private static CommentThreadAccess RequestAccess(RequestThreadFacts facts) =>
        RequestThread with { ClosedReason = JobThreadSync.OpenOnRequest(facts.State) ? null : RequestThreadClosed };

    private async Task<RequestThreadFacts?> RequestThreadFactsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Orders.AsNoTracking()
            .Where(request => request.Id == id)
            .Select(request => new RequestThreadFacts(request.State, request.CrossTenantId, request.Supplier.LinkedTenantId, request.Supplier.Name))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<CommentWriter?> WriterAsync(CancellationToken cancellationToken)
    {
        if (this.runtime.Current is not { EffectiveUserId: { } self, TenantId: { } tenantId })
            return null;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Users.AsNoTracking()
            .Where(user => user.Id == self && user.TenantId == tenantId)
            .Select(user => new CommentWriter(user.Id, user.FirstName, user.LastName, user.Email))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed record RequestThreadFacts(OrderState State, Guid? Correlation, Guid SupplierTenantId, string Supplier);

    public async Task<Result<OrderDetails>> ReadRequestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (await RefusalAsync(Readers, NoView, cancellationToken) is { } refusal)
            return Result<OrderDetails>.Failure(refusal);

        var userId = this.runtime.Current.EffectiveUserId;
        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var found = await context.Orders.AsNoTracking()
                .Where(request => request.Id == id)
                .Select(request => new
                {
                    request.Id,
                    request.Name,
                    request.Description,
                    request.PeriodStart,
                    request.PeriodEnd,
                    request.Schedule,
                    request.StartTime,
                    request.EndTime,
                    request.State,
                    request.PlaceName,
                    request.StreetAddress,
                    request.PostalCode,
                    request.City,
                    Supplier = request.Supplier.Name,
                    Orderer = request.Orderer.FirstName + " " + request.Orderer.LastName,
                    request.OrdererId,
                    request.Created,
                })
                .FirstOrDefaultAsync(token);
            if (found is null)
            {
                return Result<OrderDetails>.Failure(NotFound);
            }

            var sent = await context.StateHistoryEntries.AsNoTracking()
                .Where(entry => entry.EntityType == nameof(Order) && entry.EntityId == id && entry.TransitionKey == OrderWorkflowAttribute.SendStep)
                .OrderBy(entry => entry.Created)
                .Select(entry => (DateTime?)entry.Created)
                .FirstOrDefaultAsync(token);
            IReadOnlyList<string>? staffMembers = null;
            if (OrderSync.ShowsStaffMembers(found.State))
            {
                staffMembers = await context.OrderStaff.AsNoTracking()
                    .Where(staff => staff.OrderId == id)
                    .OrderBy(staff => staff.Position)
                    .Select(staff => staff.Name)
                    .ToListAsync(token);
            }

            return Result<OrderDetails>.Success(new OrderDetails(found.Id, found.Name, found.Description, found.PeriodStart, found.PeriodEnd, found.State, found.Supplier, found.Orderer, found.OrdererId == userId, found.Created, sent, staffMembers)
            {
                Place = new JobPlace(found.PlaceName, found.StreetAddress, found.PostalCode, found.City),
                Times = new JobTimes(found.Schedule, found.PeriodStart, found.PeriodEnd, found.StartTime, found.EndTime, []),
            });
        }, this.log, $"the order {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<DetachedEntityUpdater<Order>>> LoadOrderUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(Readers, NoView, cancellationToken) is { } refusal)
            return Result<DetachedEntityUpdater<Order>>.Failure(refusal);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var request = await context.Orders
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return request is null
                ? Result<DetachedEntityUpdater<Order>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<Order>>.Success(new DetachedEntityUpdater<Order>(request, context, UpdaterOptions));
        }, this.log, $"the order {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<Order>> NewOrderAsync(CancellationToken cancellationToken)
    {
        if (await RefusalAsync([RolePermission.OrdersCreate], NoCreate, cancellationToken) is { } refusal)
            return Result<Order>.Failure(refusal);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } userId)
            return Result<Order>.Failure(IRuntimeExecutionContext.NoTenant);

        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<Order>.Failure(today.Error);

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var supplier = await ActiveSupplierAsync(context, tenantId, cancellationToken);
        return supplier is null
            ? Result<Order>.Failure(NoSupplier)
            : Result<Order>.Success(new Order
            {
                TenantId = tenantId,
                State = OrderState.Create,
                SupplierId = supplier.Id,
                Supplier = supplier,
                OrdererId = userId,
                Schedule = JobSchedule.Single,
                PeriodStart = today.Value,
                PeriodEnd = today.Value,
            });
    }

    public async Task<Result<Guid>> CreateOrderAsync(Order request, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await RefusalAsync([RolePermission.OrdersCreate], NoCreate, cancellationToken) is { } refusal)
            return Result<Guid>.Failure(refusal);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } userId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        if (transitionKey != OrderWorkflowAttribute.SaveDraftStep)
            return Result<Guid>.Failure(CreateBySave);

        try
        {
            Guid supplierId;
            await using (var context = this.dbFactory.CreateDbContext(this.runtime))
            {
                if (await ActiveSupplierAsync(context, tenantId, cancellationToken) is not { } supplier)
                    return Result<Guid>.Failure(NoSupplier);
                supplierId = supplier.Id;
            }

            var row = new Order
            {
                TenantId = tenantId,
                State = OrderState.Create,
                Name = request.Name?.Trim() ?? string.Empty,
                Description = Optional(request.Description),
                Schedule = request.Schedule,
                PeriodStart = request.PeriodStart,
                PeriodEnd = request.PeriodEnd,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                PlaceName = Optional(request.PlaceName),
                StreetAddress = Optional(request.StreetAddress),
                PostalCode = Optional(request.PostalCode),
                City = Optional(request.City),
                SupplierId = supplierId,
                OrdererId = userId,
            };
            JobRules.Normalize(row);
            if (DescribeInvalidText(row) is { } textReason)
                return Result<Guid>.Failure(textReason);

            return await this.workflows.CreateAndTransitionAsync<OrderState, Order>(row, transitionKey, OrderWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Writing an order in tenant {TenantId} failed.", tenantId);
            return Result<Guid>.Failure("An error occurred while saving the order.");
        }
    }

    public async Task<Result> UpdateOrderAndTransitionAsync(DetachedEntityUpdater<Order> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (await RefusalAsync(Steppers, NoStep, cancellationToken) is { } refusal)
            return Result.Failure(refusal);

        var id = (Guid)updater.OriginalValue(nameof(Order.Id))!;
        try
        {
            var request = updater.Entity;
            Tidy(request, updater);
            JobRules.Normalize(request);
            if (updater.HasChanges() && DescribeInvalidText(request) is { } textReason)
                return Result.Failure(textReason);

            if (updater.HasChanges() && await DescribeInvalidLinesAsync(id, request, cancellationToken) is { } linesReason)
                return Result.Failure(linesReason);

            return await this.workflows.SaveAndTransitionAsync<OrderState, Order>(updater, transitionKey, OrderWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving the order {RequestId} failed.", id);
            return Result.Failure("An error occurred while saving the order.");
        }
        finally
        {
            this.sender.Forget(id);
        }
    }

    public async Task<Result<StateHistoryPage>> GetOrderHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await RefusalAsync(Readers, NoView, cancellationToken) is { } refusal
            ? Result<StateHistoryPage>.Failure(refusal)
            : await this.history.GetHistoryAsync<Order>(id, query, cancellationToken);

    public async Task<Result<OrderEditorOptions>> GetOrderEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<OrderEditorOptions>.Failure(InsidePortalOnly);

        var writes = await this.permissionService.HasPermissionAsync(RolePermission.OrdersCreate, cancellationToken);
        if (id is null && !writes)
            return Result<OrderEditorOptions>.Failure(NoCreate);

        if (id is not null && !writes && !await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<OrderEditorOptions>.Failure(NoView);

        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } userId)
            return Result<OrderEditorOptions>.Failure(IRuntimeExecutionContext.NoTenant);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            if (id is { } stored)
            {
                var names = await context.Orders.AsNoTracking()
                    .Where(request => request.Id == stored)
                    .Select(request => new { Supplier = request.Supplier.Name, Orderer = request.Orderer.FirstName + " " + request.Orderer.LastName })
                    .FirstOrDefaultAsync(cancellationToken);
                if (names is null)
                    return Result<OrderEditorOptions>.Failure(NotFound);

                var named = await context.OrderLines.AsNoTracking().Where(line => line.OrderId == stored).Select(line => line.OfferedServiceId).ToListAsync(cancellationToken);
                return Result<OrderEditorOptions>.Success(new OrderEditorOptions(!writes, names.Supplier, names.Orderer) { Services = await ServiceChoicesAsync(tenantId, named, cancellationToken) });
            }

            var supplier = await ActiveSupplierAsync(context, tenantId, cancellationToken);
            var orderer = await context.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => user.FirstName + " " + user.LastName).FirstOrDefaultAsync(cancellationToken);
            return Result<OrderEditorOptions>.Success(new OrderEditorOptions(false, supplier?.Name, orderer) { Services = await ServiceChoicesAsync(tenantId, [], cancellationToken) });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the order dialog's options in tenant {TenantId} failed.", tenantId);
            return Result<OrderEditorOptions>.Failure(ReadFailed);
        }
    }

    private async Task<string?> RefusalAsync(IReadOnlyList<RolePermission> permissions, string refusal, CancellationToken cancellationToken)
    {
        if (this.runtime.Current.HasPlatformReach())
            return InsidePortalOnly;

        return await this.permissionService.HasAnyPermissionAsync(permissions, cancellationToken) ? null : refusal;
    }

    private async Task<IReadOnlyList<OfferedServiceChoice>> ServiceChoicesAsync(Guid tenantId, IReadOnlyCollection<Guid> named, CancellationToken cancellationToken)
    {
        var copies = await this.lookupCache.GetAsync(OfferedCatalogue, tenantId, cancellationToken);
        return [.. copies
            .Where(copy => copy.Offered || named.Contains(copy.Id))
            .Select(copy => new OfferedServiceChoice(copy.Id, copy.Name, copy.Description, copy.DefaultSlotCount, copy.Offered))];
    }

    private async Task<string?> DescribeInvalidLinesAsync(Guid id, Order request, CancellationToken cancellationToken)
    {
        var lines = request.Lines.ToList();
        if (lines.Count > OrderLine.MaxLines
            || lines.Any(line => line.Count is < OrderLine.MinCount or > OrderLine.MaxCount)
            || lines.Select(line => line.OfferedServiceId).Distinct().Count() != lines.Count)
            return LinesInvalid;

        if (lines.Count == 0)
            return null;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var wanted = lines.Select(line => line.OfferedServiceId).ToList();
        var supplierId = request.SupplierId;
        var copies = await context.OfferedServices.AsNoTracking()
            .Where(copy => wanted.Contains(copy.Id) && copy.SupplierId == supplierId)
            .Select(copy => new { copy.Id, copy.Offered })
            .ToListAsync(cancellationToken);
        if (copies.Count != wanted.Count)
            return LinesInvalid;

        var stored = await context.OrderLines.AsNoTracking().Where(line => line.OrderId == id).Select(line => line.OfferedServiceId).ToListAsync(cancellationToken);
        return copies.Any(copy => !copy.Offered && !stored.Contains(copy.Id)) ? LineNotOffered : null;
    }

    private static async Task<List<OfferedServiceRow>> LoadOfferedAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await context.OfferedServices.AsNoTracking()
            .Where(copy => copy.TenantId == tenantId)
            .OrderBy(copy => copy.Name)
            .ThenBy(copy => copy.Id)
            .Select(copy => new OfferedServiceRow(copy.Id, copy.TenantId, copy.SupplierId, copy.Name, copy.Description, copy.DefaultSlotCount, copy.Offered))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows;
    }

    private static Task<Supplier?> ActiveSupplierAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken) =>
        context.Suppliers.AsNoTracking()
            .Where(supplier => supplier.TenantId == tenantId && supplier.State == SupplierState.Active)
            .OrderBy(supplier => supplier.Name)
            .ThenBy(supplier => supplier.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static void Tidy(Order request, DetachedEntityUpdater<Order> updater)
    {
        if (!string.Equals(request.Name, updater.OriginalValue(nameof(Order.Name)) as string, StringComparison.Ordinal))
        {
            request.Name = request.Name?.Trim() ?? string.Empty;
        }

        if (!string.Equals(request.Description, updater.OriginalValue(nameof(Order.Description)) as string, StringComparison.Ordinal))
        {
            request.Description = Optional(request.Description);
        }

        if (!string.Equals(request.PlaceName, updater.OriginalValue(nameof(Order.PlaceName)) as string, StringComparison.Ordinal))
        {
            request.PlaceName = Optional(request.PlaceName);
        }

        if (!string.Equals(request.StreetAddress, updater.OriginalValue(nameof(Order.StreetAddress)) as string, StringComparison.Ordinal))
        {
            request.StreetAddress = Optional(request.StreetAddress);
        }

        if (!string.Equals(request.PostalCode, updater.OriginalValue(nameof(Order.PostalCode)) as string, StringComparison.Ordinal))
        {
            request.PostalCode = Optional(request.PostalCode);
        }

        if (!string.Equals(request.City, updater.OriginalValue(nameof(Order.City)) as string, StringComparison.Ordinal))
        {
            request.City = Optional(request.City);
        }
    }

    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? DescribeInvalidText(Order request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return NameMissing;

        if (request.PlaceName?.Length > Job.PlaceNameMaxLength || request.StreetAddress?.Length > Job.StreetAddressMaxLength
            || request.PostalCode?.Length > Job.PostalCodeMaxLength || request.City?.Length > Job.CityMaxLength)
            return PlaceTooLong;

        return request.Name.Length > Order.NameMaxLength || request.Description?.Length > Order.DescriptionMaxLength
            ? TextTooLong
            : null;
    }
}

public sealed record OfferedServiceRow(Guid Id, Guid? TenantId, Guid SupplierId, string Name, string? Description, int DefaultSlotCount, bool Offered) : ILookupRow;
