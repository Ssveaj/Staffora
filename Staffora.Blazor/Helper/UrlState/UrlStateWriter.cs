using Microsoft.JSInterop;

namespace Staffora.Blazor.Helper.UrlState;

public interface IUrlStateWriter
{
    ValueTask ApplyAsync(string path, IReadOnlyDictionary<string, string?> changes);
}

public sealed class JsUrlStateWriter : IUrlStateWriter, IAsyncDisposable, IDisposable
{
    public const string ModulePath = "./scripts/url-state.js";

    private readonly SemaphoreSlim gate = new(1, 1);
    private IJSObjectReference? module;
    private bool disposed;
    private readonly IJSRuntime js;

    public JsUrlStateWriter(IJSRuntime js)
    {
        this.js = js;
    }

    public async ValueTask ApplyAsync(string path, IReadOnlyDictionary<string, string?> changes)
    {
        if (this.disposed || changes.Count == 0)
        {
            return;
        }

        await this.gate.WaitAsync();
        try
        {
            this.module ??= await this.js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await this.module.InvokeVoidAsync("apply", path, changes);
        }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            this.gate.Release();
        }
    }

    public void Dispose() => this.disposed = true;

    public async ValueTask DisposeAsync()
    {
        this.disposed = true;
        try
        {
            if (this.module is not null)
            {
                await this.module.DisposeAsync();
            }
        }
        catch (Exception exception) when (exception is JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
        {
        }
    }
}
