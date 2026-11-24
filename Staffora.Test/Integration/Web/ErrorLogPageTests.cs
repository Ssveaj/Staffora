using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
using Staffora.Core.Entities.Platform;
using Staffora.Services.Platform;
using Staffora.Services.Diagnostics;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using ErrorLogPage = Staffora.Blazor.Pages.SuperAdmin.ErrorLog;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class ErrorLogPageTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public ErrorLogPageTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_row_opens_its_dialog_with_its_exception_and_the_dropped_count_is_shown()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var marker = $"page-{Guid.NewGuid():N}";
        await using (var system = this.database.CreateSystemContext())
        {
            system.ErrorLogs.Add(new ErrorLog
            {
                Occurred = TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(1),
                Level = ErrorLogLevel.Error,
                Category = "test",
                Message = marker,
                ExceptionType = "System.InvalidOperationException",
                ExceptionDetail = $"System.InvalidOperationException: {marker}-exception",
            });
            await system.SaveChangesAsync(cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        host.Get<ErrorLogQueue>().TryEnqueue(new ErrorLog());
        for (var i = 0; i < ErrorLogQueue.Capacity; i++)
        {
            host.Get<ErrorLogQueue>().TryEnqueue(new ErrorLog());
        }

        using var page = new PageContext();
        page.Services.AddSingleton(host.Get<PlatformConsoleService>());
        page.Services.GetRequiredService<NavigationManager>().NavigateTo($"/superadmin/errorlog?q={marker}");
        var rendered = page.Render(Hosted);
        rendered.WaitForState(() => rendered.FindAll("tr.list-row").Any(candidate => candidate.TextContent.Contains(marker, StringComparison.Ordinal)), TimeSpan.FromSeconds(10));

        Assert.Contains("1 error was dropped", rendered.Find(".error-log-dropped").TextContent);
        Assert.DoesNotContain($"{marker}-exception", rendered.Markup);

        var row = rendered.FindAll("tr.list-row").First(candidate => candidate.TextContent.Contains(marker, StringComparison.Ordinal));
        await rendered.InvokeAsync(() => row.Click());

        rendered.WaitForAssertion(() => Assert.Contains($"{marker}-exception", rendered.Find(".error-event .error-log-exception").TextContent), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_period_level_and_search_the_address_names_decide_the_rows_shown()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var marker = $"filters-{Guid.NewGuid():N}";
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        await using (var system = this.database.CreateSystemContext())
        {
            system.ErrorLogs.AddRange(
                new ErrorLog { Occurred = now.AddMinutes(-5), Level = ErrorLogLevel.Critical, Category = "test", Message = $"{marker} critical today" },
                new ErrorLog { Occurred = now.AddDays(-20), Level = ErrorLogLevel.Critical, Category = "test", Message = $"{marker} critical long ago" },
                new ErrorLog { Occurred = now.AddMinutes(-5), Level = ErrorLogLevel.Error, Category = "test", Message = $"{marker} error today" },
                new ErrorLog { Occurred = now.AddMinutes(-5), Level = ErrorLogLevel.Critical, Category = "test", Message = $"unmarked critical {Guid.NewGuid():N}" });
            await system.SaveChangesAsync(cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        using var page = new PageContext();
        page.Services.AddSingleton(host.Get<PlatformConsoleService>());
        page.Services.GetRequiredService<NavigationManager>().NavigateTo($"/superadmin/errorlog?days=30&level=critical&q={marker}");

        var rendered = page.Render(Hosted);
        rendered.WaitForState(() => rendered.FindAll("tr.list-row").Count > 0, TimeSpan.FromSeconds(10));
        var shown = rendered.FindAll(".error-log-message__text").Select(message => message.TextContent).ToList();

        Assert.Equal([$"{marker} critical today", $"{marker} critical long ago"], shown);

        Assert.Equal(2, rendered.FindAll("tbody.list-section").Count);
    }

    private static void Hosted(RenderTreeBuilder builder)
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<ErrorLogPage>(1);
        builder.CloseComponent();
    }

    private sealed class PageContext : WebTestContext;
}
