using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Core.Models;

namespace Staffora.Services.Authorization;

public sealed class ImpersonationService
{
    public const string NotSignedIn = "Sign in to end an impersonation.";

    private readonly IRuntimeExecutionContext runtime;
    private readonly ImpersonationAuthorizer authorizer;
    private readonly ImpersonationAuditWriter audit;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly ILogger<ImpersonationService> logger;

    public ImpersonationService(IRuntimeExecutionContext runtime, ImpersonationAuthorizer authorizer, ImpersonationAuditWriter audit, UserManager<ApplicationUser> userManager, ILogger<ImpersonationService> logger)
    {
        this.runtime = runtime;
        this.authorizer = authorizer;
        this.audit = audit;
        this.userManager = userManager;
        this.logger = logger;
    }

    public async Task<Result<IReadOnlyList<ImpersonationCandidate>>> ListEligibleTargetsAsync(CancellationToken cancellationToken = default)
    {
        if (OwnerWithReach() is not { } ownerId)
        {
            return Result<IReadOnlyList<ImpersonationCandidate>>.Failure(ImpersonationAuthorizer.NotPlatformOwner);
        }

        return await this.authorizer.ListEligibleTargetsAsync(ownerId, cancellationToken);
    }

    public async Task<Result<ClaimsPrincipal>> StartAsync(ClaimsPrincipal cookie, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        if (SignedInUserOf(cookie) is not { } actingUserId)
        {
            return Result<ClaimsPrincipal>.Failure(ImpersonationAuthorizer.NotPlatformOwner);
        }

        var authorized = await this.authorizer.AuthorizeAsync(actingUserId, targetUserId, cancellationToken);
        if (!authorized.IsSuccess)
        {
            return Result<ClaimsPrincipal>.Failure(authorized.Error);
        }

        var target = authorized.Value!;
        var current = ImpersonationClaims.Read(cookie);
        Guid sessionId;
        if (current is not null && current.TargetUserId == target.UserId && await this.audit.IsOpenAsync(actingUserId, target.UserId, current.SessionId, cancellationToken))
        {
            sessionId = current.SessionId;
            this.logger.LogInformation("User {ActingUserId} already impersonates user {TargetUserId} (session {SessionId}); no second session was started.", actingUserId, target.UserId, sessionId);
        }
        else
        {
            var recorded = await this.audit.RecordStartAsync(actingUserId, target, current?.SessionId, cancellationToken);
            if (!recorded.IsSuccess)
            {
                return Result<ClaimsPrincipal>.Failure(recorded.Error);
            }

            sessionId = recorded.Value;
            this.logger.LogInformation("User {ActingUserId} started impersonating user {TargetUserId} (session {SessionId}).", actingUserId, target.UserId, sessionId);
        }

        var roles = await RolesOfAsync(target.UserId);
        return Result<ClaimsPrincipal>.Success(ImpersonationClaims.Reissue(cookie, roles, new ImpersonationClaim(target.UserId, sessionId)));
    }

    public async Task<Result> AbandonStartAsync(ClaimsPrincipal cookie, ClaimsPrincipal started, CancellationToken cancellationToken = default)
    {
        if (SignedInUserOf(started) is not { } actingUserId
            || ImpersonationClaims.Read(started) is not { } impersonation
            || ImpersonationClaims.Read(cookie)?.SessionId == impersonation.SessionId)
        {
            return Result.Success();
        }

        this.logger.LogWarning("Closing impersonation session {SessionId} of user {ActingUserId}: its cookie could not be issued.", impersonation.SessionId, actingUserId);
        return await this.audit.RecordEndAsync(actingUserId, impersonation.SessionId, cancellationToken);
    }

    public async Task<Result<ClaimsPrincipal>> EndAsync(ClaimsPrincipal cookie, CancellationToken cancellationToken = default)
    {
        if (SignedInUserOf(cookie) is not { } actingUserId)
        {
            return Result<ClaimsPrincipal>.Failure(NotSignedIn);
        }

        if (!ImpersonationClaims.IsPresent(cookie) && !await this.authorizer.IsPlatformOwnerAsync(actingUserId, cancellationToken))
        {
            return Result<ClaimsPrincipal>.Failure(ImpersonationAuthorizer.NotPlatformOwner);
        }

        var (ended, recorded) = await CloseOwnRowAsync(actingUserId, cookie, cancellationToken);
        if (!recorded.IsSuccess)
        {
            return Result<ClaimsPrincipal>.Failure(recorded.Error);
        }

        if (ended is not null)
        {
            this.logger.LogInformation("User {ActingUserId} ended impersonating user {TargetUserId} (session {SessionId}).", actingUserId, ended.TargetUserId, ended.SessionId);
        }

        var roles = await RolesOfAsync(actingUserId);
        return Result<ClaimsPrincipal>.Success(ImpersonationClaims.Reissue(cookie, roles, impersonation: null));
    }

    public async Task EndOnSignOutAsync(ClaimsPrincipal cookie, CancellationToken cancellationToken = default)
    {
        if (SignedInUserOf(cookie) is not { } actingUserId)
        {
            return;
        }

        var (ended, recorded) = await CloseOwnRowAsync(actingUserId, cookie, cancellationToken);
        if (ended is not null && recorded.IsSuccess)
        {
            this.logger.LogInformation("User {ActingUserId} signed out while impersonating user {TargetUserId}; session {SessionId} ended.", actingUserId, ended.TargetUserId, ended.SessionId);
        }
    }

    private async Task<(ImpersonationClaim? Ended, Result Recorded)> CloseOwnRowAsync(Guid actingUserId, ClaimsPrincipal cookie, CancellationToken cancellationToken)
    {
        if (ImpersonationClaims.Read(cookie) is not { } impersonation || !await this.audit.IsOpenAsync(actingUserId, impersonation.TargetUserId, impersonation.SessionId, cancellationToken))
        {
            return (null, Result.Success());
        }

        return (impersonation, await this.audit.RecordEndAsync(actingUserId, impersonation.SessionId, cancellationToken));
    }

    private Guid? OwnerWithReach() => this.runtime.Current is var session && session.HasPlatformReach() ? session.UserId : null;

    private Guid? SignedInUserOf(ClaimsPrincipal cookie) =>
        cookie.Identity?.IsAuthenticated is true && Guid.TryParse(this.userManager.GetUserId(cookie), out var userId) ? userId : null;

    private async Task<IList<string>> RolesOfAsync(Guid userId) =>
        await this.userManager.FindByIdAsync(userId.ToString()) is { } user ? await this.userManager.GetRolesAsync(user) : [];
}
