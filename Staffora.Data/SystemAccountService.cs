using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Core.Models;

namespace Staffora.Data;

public sealed class SystemAccountService
{
    private readonly IApplicationDbContextFactory<ApplicationDbContext> contexts;
    private readonly ILogger<SystemAccountService> logger;

    public SystemAccountService(IApplicationDbContextFactory<ApplicationDbContext> contexts, ILogger<SystemAccountService> logger)
    {
        this.contexts = contexts;
        this.logger = logger;
    }

    public async Task<Result<Guid>> EnsureAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var ensured = await EnsureOneAsync(tenantId, cancellationToken);
        return ensured.IsSuccess
            ? Result<Guid>.Success(SystemAccount.IdFor(tenantId))
            : Result<Guid>.Failure(ensured.Error!);
    }

    public async Task<Result<SystemAccountEnsureSummary>> EnsureForEveryTenantAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> tenantIds;
        try
        {
            await using var context = this.contexts.CreateSystemDbContext();
            using var lease = context.BeginSystemScope("system accounts: list every tenant");
            tenantIds = await context.Tenants.Select(tenant => tenant.Id).ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException)
        {
            this.logger.LogError(exception, "Could not list the tenants whose system accounts are ensured.");
            return Result<SystemAccountEnsureSummary>.Failure("The tenants' system accounts could not be ensured.");
        }

        var created = 0;
        var failed = new List<Guid>();
        foreach (var tenantId in tenantIds)
        {
            var ensured = await EnsureOneAsync(tenantId, cancellationToken);
            if (!ensured.IsSuccess)
            {
                failed.Add(tenantId);
            }
            else if (ensured.Value)
            {
                created++;
            }
        }

        if (failed.Count > 0)
        {
            this.logger.LogError("Ensured the system accounts of {TenantCount} tenants: {Created} created, {FailedCount} failed ({FailedTenantIds}).", tenantIds.Count, created, failed.Count, failed);
        }
        else
        {
            this.logger.LogInformation("Ensured the system accounts of {TenantCount} tenants: {Created} created.", tenantIds.Count, created);
        }

        return Result<SystemAccountEnsureSummary>.Success(new SystemAccountEnsureSummary(created, failed));
    }

    public static async Task<bool> EnsureAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = SystemAccount.IdFor(tenantId);

        using var lease = context.BeginSystemScope("ensure a tenant's system account");
        if (await IsInPlaceAsync(context, id, tenantId, cancellationToken))
        {
            return false;
        }

        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            throw new InvalidOperationException("A system account is ensured only for an existing tenant.");
        }

        var account = new ApplicationUser
        {
            Id = id,
            UserName = SystemAccount.UserNameFor(tenantId),
            NormalizedUserName = SystemAccount.UserNameFor(tenantId).ToUpperInvariant(),
            FirstName = SystemAccount.FirstName,
            LastName = SystemAccount.LastName,
            IsSystemAccount = true,
            State = UserState.Active,
            TenantId = tenantId,
            SecurityStamp = Guid.NewGuid().ToString("N"),

            CreatedById = id,
        };
        context.Users.Add(account);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException) when (context.Database.CurrentTransaction is null)
        {
            context.Entry(account).State = EntityState.Detached;
            if (await IsInPlaceAsync(context, id, tenantId, cancellationToken))
            {
                return false;
            }

            throw;
        }
    }

    private async Task<Result<bool>> EnsureOneAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = this.contexts.CreateSystemDbContext();
            var created = await EnsureAsync(context, tenantId, cancellationToken);
            if (created)
            {
                this.logger.LogInformation("Created the system account of tenant {TenantId}.", tenantId);
            }

            return Result<bool>.Success(created);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException)
        {
            this.logger.LogError(exception, "Could not ensure the system account of tenant {TenantId}.", tenantId);
            return Result<bool>.Failure("The tenant's system account could not be ensured.");
        }
    }

    private static async Task<bool> IsInPlaceAsync(ApplicationDbContext context, Guid id, Guid tenantId, CancellationToken cancellationToken)
    {
        var existing = await context.Users.AsNoTracking()
            .Where(user => user.Id == id)
            .Select(user => new { user.IsSystemAccount, user.TenantId, user.UserName, user.PasswordHash, user.Email })
            .FirstOrDefaultAsync(cancellationToken);

        return existing switch
        {
            null => false,
            { IsSystemAccount: false } => throw new InvalidOperationException("The id of a tenant's system account is held by another user."),
            _ when existing.TenantId == tenantId
                && existing.UserName == SystemAccount.UserNameFor(tenantId)
                && existing.PasswordHash is null
                && existing.Email is null => true,
            _ => throw new InvalidOperationException("The row holding the id of a tenant's system account is not the account as an ensure writes it: another tenant or user name, a password or an address."),
        };
    }
}

public sealed record SystemAccountEnsureSummary(int Created, IReadOnlyList<Guid> FailedTenantIds);
