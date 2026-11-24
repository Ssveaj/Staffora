using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities.Comments;
using Staffora.Data;
using Staffora.E2ESetup;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.E2E.DialogHost;

internal static class DialogHostServer
{
    public const string Command = "dialog-host";

    public const string UrlVariable = "STAFFORA_E2E_DIALOG_HOST_URL";

    public const string DefaultUrl = "http://localhost:5281";

    public const string StateFileName = "dialog-host-state.json";

    public const string StopPath = "/test/dialog-host/stop";

    public static async Task<int> RunAsync()
    {
        var url = new Uri(Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } configured ? configured : DefaultUrl);
        var statePath = Path.Combine(FoundationStateFile.FindRepositoryRoot(), ".e2e", StateFileName);
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
        }

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopped.TrySetResult();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stopped.TrySetResult();

        await using var pool = new DatabasePool();
        var leaser = new RealDatabase(pool);
        var database = await leaser.CreateIsolatedAsync("dialoghost", CancellationToken.None);
        var name = database.Name;
        try
        {
            Console.WriteLine($"dialog-host: database {name} is ready.");
            var state = await SeedAsync(database);
            await using (AppHost.StartOnKestrel(database.ConnectionString, "Development", url.Port, new Dictionary<string, string> { [StartupSeeding.SkipSetting] = "1" }, services =>
            {
                AddTestDialogs(services);
                services.AddSingleton<IStartupFilter>(new StopRoute(stopped));
            }))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state with { BaseUrl = url.GetLeftPart(UriPartial.Authority) }, FoundationStateFile.JsonOptions));
                Console.WriteLine($"dialog-host: the app is listening on {url.GetLeftPart(UriPartial.Authority)}; state in {statePath}.");

                await stopped.Task;
                Console.WriteLine("dialog-host: stopping.");
            }
        }
        finally
        {
            await database.DisposeAsync();
            Console.WriteLine($"dialog-host: database {name} dropped.");
            File.Delete(statePath);
        }

        return 0;
    }

    private sealed class StopRoute : IStartupFilter
    {
        private readonly TaskCompletionSource stopped;

        public StopRoute(TaskCompletionSource stopped)
        {
            this.stopped = stopped;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path == StopPath && HttpMethods.IsPost(context.Request.Method) && AppHost.IsLoopbackCaller(context))
                {
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    await context.Response.CompleteAsync();
                    this.stopped.TrySetResult();
                    return;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private static void AddTestDialogs(IServiceCollection services)
    {
        services.RemoveAll<IDbContextFactory<ApplicationDbContext>>();
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(provider => new TestContexts(provider.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
        services.AddTestDeleteCleanups();
        services.AddScoped<TestParentDialogService>();
        services.AddScoped<TestChildDialogService>();
        services.RemoveAll<DialogRouteRegistry>();
        services.AddSingleton(DialogRouteRegistry.FromTypes([.. typeof(Program).Assembly.GetTypes(), typeof(TestParentDialogContext), typeof(TestParentDialog), typeof(TestChildDialogContext), typeof(TestChildDialog)]));
    }

    private static async Task<DialogHostState> SeedAsync(RealDatabase database)
    {
        var scenario = await TwoTenantScenario.CreateAsync(database);
        foreach (var tenant in new[] { scenario.A, scenario.B })
        {
            await TwoTenantScenario.GrantAsync(database, tenant, tenant.OwnerId, CancellationToken.None, TestParentDialogService.ViewPermission, TestParentDialogService.ChildrenPermission);
        }

        var (parent, children) = await AddParentAsync(database, scenario.A, "Autumn job plan", "Week 1 timetable", "Week 2 timetable");
        var (foreignParent, foreignChildren) = await AddParentAsync(database, scenario.B, "Spring job plan", "Week 1 outline");
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(database);
        var (platformParent, _) = await AddParentAsync(database, platform, "Platform plan", "Platform week");
        var (reader, commented) = await AddConversationAsync(database, scenario.A);
        var longThread = await AddLongThreadAsync(database, scenario.A, reader);
        return new DialogHostState(string.Empty, scenario.A.OwnerId, parent, "Autumn job plan", children[0], "Week 1 timetable", foreignParent, foreignChildren[0], platform.OwnerId, platformParent, "Platform plan", reader, commented, "Spring period job", longThread, "Long thread job", LongThreadComments);
    }

    private const int LongThreadComments = 210;

    private static async Task<Guid> AddLongThreadAsync(RealDatabase database, ScenarioTenant tenant, Guid reader)
    {
        await using var writer = database.CreateContext(tenant.RuntimeOf(reader));
        var row = new TestParent { Name = "Long thread job" };
        writer.TestParents.Add(row);
        await writer.SaveChangesAsync();

        await using var transaction = await writer.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        for (var number = 1; number <= LongThreadComments; number++)
        {
            await CommentThreadWriter.AddAsync(writer, row, CommentAudience.Internal, CommentKind.Comment, CommentAuthorType.User, $"Long thread comment {number}.", transitionKey: null, CancellationToken.None);
        }

        await writer.SaveChangesAsync();
        await transaction.CommitAsync();
        return row.Id;
    }

    private static async Task<(Guid Reader, Guid Parent)> AddConversationAsync(RealDatabase database, ScenarioTenant tenant)
    {
        var reader = await TwoTenantScenario.AddUserAsync(database, tenant, "Robin");
        var colleague = await TwoTenantScenario.AddUserAsync(database, tenant, "Alex");
        var coordinator = await TwoTenantScenario.AddUserAsync(database, tenant, "Sam");
        await TwoTenantScenario.GrantAsync(database, tenant, reader, CancellationToken.None, TestParentDialogService.ViewPermission, TestParentDialogService.ChildrenPermission, TestParentDialogService.InternalThreadPermission, TestParentDialogService.StaffThreadPermission);
        await TwoTenantScenario.GrantAsync(database, tenant, colleague, CancellationToken.None, TestParentDialogService.ViewPermission, TestParentDialogService.InternalThreadPermission);
        await TwoTenantScenario.GrantAsync(database, tenant, coordinator, CancellationToken.None, TestParentDialogService.ViewPermission, TestParentDialogService.StaffThreadPermission, TestParentDialogService.StaffPostPermission);

        Guid parent;
        await using (var writer = database.CreateContext(tenant.Runtime()))
        {
            var row = new TestParent { Name = "Spring period job", Notes = TestParent.AsksForAComment };
            writer.TestParents.Add(row);
            await writer.SaveChangesAsync();
            parent = row.Id;
        }

        await PostAsync(database, tenant.RuntimeOf(reader), parent, CommentAudience.Internal, "Who takes the morning group in week 1?");
        await PostAsync(database, tenant.RuntimeOf(colleague), parent, CommentAudience.Internal, "I can, from Monday. The afternoon group still needs someone.");
        await using (var host = ServiceHost.Create(database, tenant.RuntimeOf(colleague)))
        {
            var annotated = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-annotate", TestParentWorkflowQueries.WithWorkflowGraph, "Moved the start to week 2 after the room change.");
            if (!annotated.IsSuccess)
            {
                throw new InvalidOperationException($"dialog-host: the parent's note could not be written: {annotated.Error}");
            }
        }

        await PostAsync(database, tenant.RuntimeOf(coordinator), parent, CommentAudience.Staff, "Can you take the Monday group?");
        return (reader, parent);
    }

    private static async Task PostAsync(RealDatabase database, Staffora.Core.Execution.IRuntimeExecutionContext runtime, Guid parent, CommentAudience audience, string text)
    {
        await using var host = ServiceHost.Create(database, runtime, services => services.AddScoped<TestParentDialogService>());
        var posted = await host.Get<TestParentDialogService>().AddTestParentCommentAsync(parent, audience, text, CancellationToken.None);
        if (!posted.IsSuccess)
        {
            throw new InvalidOperationException($"dialog-host: a comment could not be written: {posted.Error}");
        }
    }

    private static async Task<(Guid Parent, Guid[] Children)> AddParentAsync(RealDatabase database, ScenarioTenant tenant, string name, params string[] children)
    {
        await using var writer = database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("dialog host: a parent and its children as transitions left them");
        var parent = new TestParent { Name = name, State = TestParentState.Draft };
        var rows = children.Select(child => new TestChild { Name = child }).ToArray();
        foreach (var row in rows)
        {
            parent.Children.Add(row);
        }

        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync();
        return (parent.Id, [.. rows.Select(row => row.Id)]);
    }

    private sealed class TestContexts : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> options;

        public TestContexts(DbContextOptions<ApplicationDbContext> options)
        {
            this.options = new DbContextOptionsBuilder<ApplicationDbContext>(options)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
        }

        public ApplicationDbContext CreateDbContext() => new TestDataContext(this.options);
    }
}

internal sealed record DialogHostState(string BaseUrl, Guid UserId, Guid ParentId, string ParentName, Guid ChildId, string ChildName, Guid ForeignParentId, Guid ForeignChildId, Guid PlatformOwnerId, Guid PlatformParentId, string PlatformParentName, Guid CommentReaderId, Guid CommentedParentId, string CommentedParentName, Guid LongThreadParentId, string LongThreadParentName, int LongThreadComments);
