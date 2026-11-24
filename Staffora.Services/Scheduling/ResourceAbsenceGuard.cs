using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;

namespace Staffora.Services.Scheduling;

public sealed class ResourceAbsenceGuard : IResourceAbsenceGuard, IStepPreparation
{
    private readonly Dictionary<Guid, Facts> facts = [];
    private readonly Lock gate = new();
    private readonly PermissionService permissions;
    private readonly IRuntimeExecutionContext runtime;

    public ResourceAbsenceGuard(PermissionService permissions, IRuntimeExecutionContext runtime)
    {
        this.permissions = permissions;
        this.runtime = runtime;
    }

    public Type EntityType => typeof(ResourceAbsence);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stored = await context.ResourceAbsences.AsNoTracking()
            .Where(absence => absence.Id == entityId)
            .Select(absence => (AbsenceKind?)absence.Kind)
            .FirstOrDefaultAsync(cancellationToken);
        if (stored is not { } kind)
        {
            lock (this.gate)
            {
                this.facts.Remove(entityId);
            }

            return;
        }

        var manages = await this.permissions.HasPermissionAsync(RolePermission.ManageResourceAbsence, cancellationToken);
        var reportsOwn = await this.permissions.HasPermissionAsync(RolePermission.ReportOwnAbsence, cancellationToken);
        lock (this.gate)
        {
            this.facts[entityId] = new Facts(kind, manages, reportsOwn);
        }
    }

    public bool MayChange(ResourceAbsence absence)
    {
        var facts = FactsOf(absence);
        return facts.Manages
            || (facts.ReportsOwn && absence.Resource is { } resource && this.runtime.EffectiveUserId is { } actor && resource.UserId == actor);
    }

    public bool KindChanged(ResourceAbsence absence) => absence.Kind != FactsOf(absence).StoredKind;

    public bool SessionManagesAbsence() => this.permissions.HasPermission(RolePermission.ManageResourceAbsence);

    private Facts FactsOf(ResourceAbsence absence)
    {
        ArgumentNullException.ThrowIfNull(absence);
        lock (this.gate)
        {
            return this.facts.TryGetValue(absence.Id, out var facts)
                ? facts
                : throw new InvalidOperationException($"Nothing was read about the absence {absence.Id} before its steps were judged, so who may change it cannot be checked.");
        }
    }

    private sealed record Facts(AbsenceKind StoredKind, bool Manages, bool ReportsOwn);
}
