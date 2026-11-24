using Staffora.Blazor.Pages.SuperAdmin;
using Staffora.Core.Models;

namespace Staffora.Test.Web.Pages;

public class ErrorLogTextTests
{
    [Fact]
    public void Who_an_error_acted_for_names_the_impersonator_and_a_removed_user()
    {
        Assert.Equal("priya.raman, impersonated by admin", ErrorLogText.WhoText(new ErrorLogEntryDto { UserId = Guid.NewGuid(), UserName = "priya.raman", ImpersonatorId = Guid.NewGuid(), ImpersonatorName = "admin" }));
        Assert.Equal("A removed user", ErrorLogText.WhoText(new ErrorLogEntryDto { UserId = Guid.NewGuid() }));
        Assert.Equal("No user", ErrorLogText.WhoText(new ErrorLogEntryDto()));
        Assert.Equal("Removed tenant", ErrorLogText.TenantText(new ErrorLogEntryDto { TenantId = Guid.NewGuid() }));
        Assert.Equal("IntegrationOutboxDispatcher", ErrorLogText.ShortName("Staffora.Services.Integration.IntegrationOutboxDispatcher"));
    }
}
