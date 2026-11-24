using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Blazor.Helper.UrlState;

namespace Staffora.Test.Web.UrlState;

internal sealed class TestBrowser : NavigationManager
{
    private const string Origin = "https://staffora.test";

    public TestBrowser(string pathAndQuery)
    {
        Initialize(Origin + "/", Origin + pathAndQuery);
        Address = Uri;
        Writer = new AddressWriter(this);
    }

    public string Address { get; private set; }

    public string PathAndQuery => new Uri(Address).PathAndQuery;

    public AddressWriter Writer { get; }

    public string? Query(string key) =>
        QueryHelpers.ParseQuery(new Uri(Address).Query).TryGetValue(key, out var values) ? values[^1] : null;

    public UrlStateService CreateService() => new(this, Writer, NullLogger<UrlStateService>.Instance);

    public void Navigate(string pathAndQuery, bool raiseEvent = true)
    {
        Uri = Origin + pathAndQuery;
        Address = Uri;
        if (raiseEvent)
        {
            RaiseLocationChanged();
        }
    }

    public void RaiseLocationChanged() => NotifyLocationChanged(isInterceptedLink: false);

    protected override void NavigateToCore(string uri, NavigationOptions options) => Navigate(new Uri(ToAbsoluteUri(uri).ToString()).PathAndQuery);

    internal void Apply(string path, IReadOnlyDictionary<string, string?> changes)
    {
        var current = new Uri(Address);
        if (current.AbsolutePath != path)
        {
            return;
        }

        var pairs = QueryHelpers.ParseQuery(current.Query).Select(pair => (pair.Key, Value: pair.Value[^1] ?? string.Empty)).ToList();
        foreach (var (key, value) in changes)
        {
            var index = pairs.FindIndex(pair => pair.Key == key);
            if (string.IsNullOrEmpty(value))
            {
                if (index >= 0)
                {
                    pairs.RemoveAt(index);
                }
            }
            else if (index >= 0)
            {
                pairs[index] = (key, value);
            }
            else
            {
                pairs.Add((key, value));
            }
        }

        var query = string.Join('&', pairs.Select(pair => $"{System.Uri.EscapeDataString(pair.Key)}={System.Uri.EscapeDataString(pair.Value)}"));
        Address = Origin + current.AbsolutePath + (query.Length > 0 ? "?" + query : string.Empty);
    }
}

internal sealed class AddressWriter : IUrlStateWriter
{
    private readonly TestBrowser browser;

    public AddressWriter(TestBrowser browser)
    {
        this.browser = browser;
    }

    public List<(string Path, Dictionary<string, string?> Changes)> Writes { get; } = [];

    public bool FailNext { get; set; }

    public ValueTask ApplyAsync(string path, IReadOnlyDictionary<string, string?> changes)
    {
        Writes.Add((path, new Dictionary<string, string?>(changes)));
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("The writer failed.");
        }

        this.browser.Apply(path, changes);
        return ValueTask.CompletedTask;
    }
}
