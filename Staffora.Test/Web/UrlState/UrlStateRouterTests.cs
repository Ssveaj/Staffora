using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Helper.UrlState;

namespace Staffora.Test.Web.UrlState;

public class UrlStateRouterTests : WebTestContext
{
    private readonly BunitJSModuleInterop module;
    private readonly ProbeLog log = new();

    public UrlStateRouterTests()
    {
        this.module = JSInterop.SetupModule(JsUrlStateWriter.ModulePath);
        this.module.SetupVoid("apply", _ => true).SetVoidResult();
        Services.AddSingleton(this.log);
    }

    [Fact]
    public async Task A_page_the_router_keeps_across_a_new_route_parameter_still_writes_its_keys()
    {
        var (host, navigation, urlState) = Start("/url-state-probe/a");

        await host.InvokeAsync(() => navigation.NavigateTo("/url-state-probe/b"));
        host.WaitForAssertion(() => Assert.Equal("b", host.Find("#probe-id").TextContent));
        Assert.Single(this.log.Pages);

        await host.InvokeAsync(() => this.log.Pages[0].SetStatus(ProbeStatus.Active));
        await urlState.WhenWrittenAsync();

        var last = this.module.Invocations["apply"][^1];
        Assert.Equal("/url-state-probe/b", last.Arguments[0]);
        Assert.Equal("active", ((IReadOnlyDictionary<string, string?>)last.Arguments[1]!)[UrlStateKeys.Status]);
    }

    [Fact]
    public async Task A_link_to_the_kept_page_with_another_query_wins_and_the_page_is_told()
    {
        var (host, navigation, urlState) = Start("/url-state-probe/a?status=active");
        Assert.Equal("Active", host.Find("#probe-status").TextContent);

        await host.InvokeAsync(() => navigation.NavigateTo("/url-state-probe/a?status=inactive"));
        host.WaitForAssertion(() => Assert.Equal("Inactive", host.Find("#probe-status").TextContent));
        await urlState.WhenWrittenAsync();

        Assert.Single(this.log.Pages);
        Assert.Equal(1, this.log.Changes);
        Assert.Equal("inactive", urlState.ReadRaw(UrlStateKeys.Status));

        Assert.DoesNotContain(this.module.Invocations["apply"], call => ((IReadOnlyDictionary<string, string?>)call.Arguments[1]!).GetValueOrDefault(UrlStateKeys.Status) == "active");
    }

    [Fact]
    public async Task A_page_the_router_replaced_neither_writes_nor_removes_its_keys()
    {
        var (host, navigation, urlState) = Start("/url-state-probe/a?status=active");
        var left = this.log.Pages[0];

        await host.InvokeAsync(() => navigation.NavigateTo("/url-state-probe-other?status=active"));
        host.WaitForElement("#probe-other");
        Assert.True(left.IsDisposed);

        await host.InvokeAsync(() => left.SetStatus(ProbeStatus.Inactive));
        await urlState.WhenWrittenAsync();

        Assert.Equal("active", urlState.ReadRaw(UrlStateKeys.Status));
        Assert.Empty(this.module.Invocations["apply"]);
    }

    private (IRenderedComponent<IComponent> Host, NavigationManager Navigation, UrlStateService UrlState) Start(string address)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(address);
        var host = Render(RouterHost);
        host.WaitForElement("#probe-status, #probe-other");
        var urlState = Services.GetRequiredService<UrlStateService>();
        urlState.MarkInteractive();
        return (host, navigation, urlState);
    }

    private static void RouterHost(RenderTreeBuilder builder)
    {
        builder.OpenComponent<Router>(0);
        builder.AddAttribute(1, nameof(Router.AppAssembly), typeof(ProbePage).Assembly);
        builder.AddAttribute(2, nameof(Router.Found), (RenderFragment<RouteData>)(route => found =>
        {
            found.OpenComponent<UrlStatePageTracker>(0);
            found.AddAttribute(1, nameof(UrlStatePageTracker.RouteData), route);
            found.CloseComponent();
            found.OpenComponent<RouteView>(2);
            found.AddAttribute(3, nameof(RouteView.RouteData), route);
            found.CloseComponent();
        }));
        builder.CloseComponent();
    }

    public enum ProbeStatus { All, Active, Inactive }

    public sealed class ProbeLog
    {
        public List<ProbePage> Pages { get; } = [];

        public int Changes { get; set; }
    }

    [Route("/url-state-probe/{Id}")]
    public sealed class ProbePage : ComponentBase, IDisposable
    {
        private static readonly UrlStateKey<ProbeStatus> statusKey = UrlStateKey.Enum(UrlStateKeys.Status, ProbeStatus.All);
        private UrlStateValue<ProbeStatus> status = null!;

        [Inject] private UrlStateService UrlState { get; set; } = null!;

        [Inject] private ProbeLog Log { get; set; } = null!;

        [Parameter] public string Id { get; set; } = string.Empty;

        public bool IsDisposed { get; private set; }

        public void SetStatus(ProbeStatus status) => this.status.Value = status;

        protected override void OnInitialized()
        {
            this.status = UrlState.Bind(statusKey);
            this.status.Changed += () =>
            {
                Log.Changes++;
                _ = InvokeAsync(StateHasChanged);
            };
            Log.Pages.Add(this);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "id", "probe-id");
            builder.AddContent(2, Id);
            builder.CloseElement();
            builder.OpenElement(3, "span");
            builder.AddAttribute(4, "id", "probe-status");
            builder.AddContent(5, this.status.Value.ToString());
            builder.CloseElement();
        }

        public void Dispose()
        {
            IsDisposed = true;
            this.status.Dispose();
        }
    }

    [Route("/url-state-probe-other")]
    public sealed class OtherProbePage : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "id", "probe-other");
            builder.CloseElement();
        }
    }
}
