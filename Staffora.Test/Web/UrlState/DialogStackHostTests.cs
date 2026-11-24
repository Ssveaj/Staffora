using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Helper;
using Staffora.Blazor.Helper.UrlState;

namespace Staffora.Test.Web.UrlState;

public class DialogStackHostTests : WebTestContext
{
    private readonly IRenderedComponent<RadzenDialog> host;
    private readonly DialogService dialogs;
    private readonly UrlStateService urlState;
    private readonly BunitJSModuleInterop module;

    public DialogStackHostTests()
    {
        this.module = JSInterop.SetupModule(JsUrlStateWriter.ModulePath);
        this.module.SetupVoid("apply", _ => true).SetVoidResult();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/superadmin/tenants?q=a");
        this.host = Render<RadzenDialog>();
        this.dialogs = Services.GetRequiredService<DialogService>();
        this.urlState = Services.GetRequiredService<UrlStateService>();
        this.urlState.MarkInteractive();
    }

    [Fact]
    public async Task A_dialog_over_a_row_is_on_the_stack_while_it_is_open_and_nested_ones_follow_it()
    {
        var tenant = new DialogStackSegment(DialogTokens.Tenant, Guid.NewGuid());
        var role = new DialogStackSegment("role", "7");

        var outer = await OpenAsync(() => this.dialogs.OpenFormDialogAsync<Probe>("Tenant A", options: new FormDialogOptions { UrlSegment = tenant }), expectedDialogs: 1);
        Assert.Equal([tenant], this.urlState.DialogStack);

        var confirm = await OpenAsync(() => this.dialogs.ConfirmAsync("Leave?"), expectedDialogs: 2);
        Assert.Equal([tenant], this.urlState.DialogStack);
        await CloseTopAsync(expectedDialogs: 1);
        Assert.False(await confirm);

        var inner = await OpenAsync(() => this.dialogs.OpenFormDialogAsync<Probe>("Role", options: new FormDialogOptions { UrlSegment = role }), expectedDialogs: 2);
        Assert.Equal([tenant, role], this.urlState.DialogStack);

        await CloseTopAsync(expectedDialogs: 1);
        Assert.Equal([tenant], this.urlState.DialogStack);
        await CloseTopAsync(expectedDialogs: 0);
        Assert.Empty(this.urlState.DialogStack);
        await Task.WhenAll(outer, inner);
        await this.urlState.WhenWrittenAsync();

        var written = this.module.Invocations["apply"].Select(call => ((string)call.Arguments[0]!, ((IReadOnlyDictionary<string, string?>)call.Arguments[1]!)[UrlStateKeys.DialogStack])).ToList();
        Assert.Equal(
            [("/superadmin/tenants", tenant.ToString()), ("/superadmin/tenants", $"{tenant}.{role}"), ("/superadmin/tenants", tenant.ToString()), ("/superadmin/tenants", null)],
            written);
        Assert.All(this.module.Invocations["apply"], call => Assert.Single((IReadOnlyDictionary<string, string?>)call.Arguments[1]!));
    }

    [Fact]
    public async Task A_dialog_without_a_row_stays_out_of_the_address()
    {
        var shown = await OpenAsync(() => this.dialogs.OpenFormDialogAsync<Probe>("A note"), expectedDialogs: 1);

        Assert.Empty(this.urlState.DialogStack);
        await CloseTopAsync(expectedDialogs: 0);
        await shown;
        await this.urlState.WhenWrittenAsync();
        Assert.Empty(this.module.Invocations["apply"]);
    }

    [Fact]
    public async Task A_navigation_closes_the_open_dialogs_and_the_new_address_names_none()
    {
        var tenant = new DialogStackSegment(DialogTokens.Tenant, Guid.NewGuid());
        var shown = await OpenAsync(() => this.dialogs.OpenFormDialogAsync<Probe>("Tenant A", options: new FormDialogOptions { UrlSegment = tenant }), expectedDialogs: 1);
        await this.urlState.WhenWrittenAsync();
        Assert.Equal([tenant], this.urlState.DialogStack);

        var navigation = Services.GetRequiredService<NavigationManager>();
        await this.host.InvokeAsync(() => navigation.NavigateTo("/superadmin/errorlog?x=1"));
        this.host.WaitForAssertion(() => Assert.Empty(this.host.FindAll(".form-dialog")));
        await shown;
        await this.urlState.WhenWrittenAsync();

        Assert.Empty(this.urlState.DialogStack);
        Assert.Null(this.urlState.ReadRaw(UrlStateKeys.DialogStack));

        Assert.DoesNotContain(this.module.Invocations["apply"], call =>
            (string)call.Arguments[0]! == "/superadmin/errorlog"
            && ((IReadOnlyDictionary<string, string?>)call.Arguments[1]!).GetValueOrDefault(UrlStateKeys.DialogStack) is not null);
    }

    private async Task<Task<T>> OpenAsync<T>(Func<Task<T>> open, int expectedDialogs)
    {
        Task<T>? answer = null;
        await this.host.InvokeAsync(() => { answer = open(); });
        this.host.WaitForAssertion(() => Assert.Equal(expectedDialogs, this.host.FindAll(".form-dialog").Count));
        return answer!;
    }

    private async Task CloseTopAsync(int expectedDialogs)
    {
        await this.host.InvokeAsync(() => this.dialogs.Close());
        this.host.WaitForAssertion(() => Assert.Equal(expectedDialogs, this.host.FindAll(".form-dialog").Count));
    }

    public sealed class Probe : ComponentBase;
}
