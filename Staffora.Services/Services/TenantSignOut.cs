using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;

namespace Staffora.Services.Services;

public sealed class TenantSignOut : ITenantSignOut
{
    private readonly TimeProvider clock;

    public TenantSignOut(TimeProvider clock)
    {
        this.clock = clock;
    }

    public async Task SignOutEveryoneAsync(DbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context is not ApplicationDbContext data || data.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A tenant's users are signed out inside the step that moves the tenant, on its own context and transaction.");
        }

        var modified = this.clock.GetUtcNow().UtcDateTime;
        var editor = SystemAccount.IdFor(tenantId);

        await data.Users
            .Where(user => user.TenantId == tenantId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(user => user.SecurityStamp, user => Guid.NewGuid().ToString())
                    .SetProperty(user => user.ConcurrencyStamp, user => Guid.NewGuid().ToString())
                    .SetProperty(user => user.Modified, modified)
                    .SetProperty(user => user.ModifiedById, editor),
                cancellationToken);
    }
}
