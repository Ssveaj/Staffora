using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Services;

namespace Staffora.Test.Integration.Infrastructure;

public static class UserEdits
{
    public static async Task<Result> SaveUserAsync(this UserService users, Guid userId, Action<ApplicationUser> edit, string step = ApplicationUserWorkflowAttribute.SaveStep)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(edit);
        var loaded = await users.LoadApplicationUserUpdaterAsync(userId, cancellationToken);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return Result.Failure(loaded.Error);
        }

        edit(loaded.Value.Entity);
        return await users.UpdateApplicationUserAndTransitionAsync(loaded.Value, step, null, cancellationToken);
    }

    public static async Task SetStateInTableAsync(RealDatabase database, Guid userId, UserState state, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        var written = await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUsers SET State = {(int)state} WHERE Id = {userId}", cancellationToken);
        if (written != 1)
        {
            throw new InvalidOperationException($"No user {userId} to put in {state}.");
        }
    }

    public static async Task DeleteInTableAsync(RealDatabase database, Guid userId, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM AspNetUsers WHERE Id = {userId}", cancellationToken);
    }

    public static Task<Result> StepUserAsync(this UserService users, Guid userId, string step) =>
        users.SaveUserAsync(userId, _ => { }, step);
}
