using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;

namespace Staffora.Blazor.Helper.UrlState;

public sealed class UrlStateService : IDisposable
{
    private readonly NavigationManager navigation;
    private readonly IUrlStateWriter writer;
    private readonly ILogger<UrlStateService> logger;
    private readonly object gate = new();

    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string?> pending = new(StringComparer.Ordinal);

    private readonly List<UrlStateBinding> bindings = [];
    private readonly List<DialogStackEntry> dialogs = [];

    private string seededUri = string.Empty;
    private string path = string.Empty;

    private string? caughtUpUri;

    private int navigationCount;

    private object page = new();
    private Type? pageType;
    private int pageNavigation;

    private IReadOnlyList<DialogStackSegment> arrivedDialogChain = [];
    private bool arrivalTaken;
    private bool interactive;
    private bool disposed;
    private bool writing;
    private TaskCompletionSource idle = CompletedSource();

    public UrlStateService(NavigationManager navigation, IUrlStateWriter writer, ILogger<UrlStateService> logger)
    {
        this.navigation = navigation;
        this.writer = writer;
        this.logger = logger;
        lock (this.gate)
        {
            ReseedLocked(navigation.Uri, navigated: false);
        }

        navigation.LocationChanged += OnLocationChanged;
    }

    public bool IsInteractive
    {
        get
        {
            lock (this.gate)
            {
                return this.interactive;
            }
        }
    }

    public string? ReadRaw(string name)
    {
        bool write;
        string? value;
        lock (this.gate)
        {
            write = CatchUpLocked();
            value = this.values.GetValueOrDefault(name);
        }

        if (write)
        {
            StartWriting();
        }

        return value;
    }

    public T Read<T>(UrlStateKey<T> key) => key.Parse(ReadRaw(key.Name));

    public UrlStateValue<T> Bind<T>(UrlStateKey<T> key, bool exclusive = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Name == UrlStateKeys.DialogStack)
        {
            throw new InvalidOperationException($"\"{UrlStateKeys.DialogStack}\" is reserved for the dialog stack; dialogs reach it through the dialog host.");
        }

        bool write;
        UrlStateValue<T> binding;
        lock (this.gate)
        {
            write = CatchUpLocked();
            var owners = this.bindings.Where(older => IsCurrentLocked(older) && older.Name == key.Name).ToList();
            if (owners.Count > 0 && (exclusive || owners.Any(older => older.Exclusive)))
            {
                throw new InvalidOperationException($"The query-string key \"{key.Name}\" is bound already on {this.path}, and a list's keys are its own: give the second list an AddressPrefix, or set its BindAddress to false.");
            }

            foreach (var older in owners)
            {
                older.Superseded = true;
                this.logger.LogWarning("The query-string key {Key} was bound twice on {Path}; the newer binding owns it.", key.Name, this.path);
            }

            binding = new UrlStateValue<T>(this, key, key.Parse(this.values.GetValueOrDefault(key.Name)), this.page, this.navigationCount) { Exclusive = exclusive };
            this.bindings.Add(binding);

            write |= QueueLocked(key.Name, binding.Text);
        }

        if (write)
        {
            StartWriting();
        }

        return binding;
    }

    public void EnterPage(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        List<UrlStateBinding> changed = [];
        bool write;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            write = CatchUpLocked();
            if (this.pageType is not null && this.pageType != pageType)
            {
                this.page = new object();
            }
            else if (this.pageNavigation != this.navigationCount)
            {
                foreach (var binding in this.bindings.Where(binding => binding.Page == this.page && binding is { Superseded: false, Disposed: false }))
                {
                    binding.Navigation = this.navigationCount;
                    if (binding.AdoptLocked(this.values.GetValueOrDefault(binding.Name)))
                    {
                        changed.Add(binding);
                    }

                    write |= QueueLocked(binding.Name, binding.Text);
                }
            }

            this.pageType = pageType;
            this.pageNavigation = this.navigationCount;
        }

        if (write)
        {
            StartWriting();
        }

        foreach (var binding in changed)
        {
            binding.RaiseChanged();
        }
    }

    public IDisposable PushDialog(DialogStackSegment segment)
    {
        if (!DialogStackSegment.IsValidToken(segment.Token) || !DialogStackSegment.IsValidId(segment.Id))
        {
            throw new ArgumentException("A dialog segment needs a token and an id.", nameof(segment));
        }

        var entry = new DialogStackEntry(this, segment);
        bool write;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return entry;
            }

            write = CatchUpLocked();
            this.dialogs.Add(entry);
            write |= QueueLocked(UrlStateKeys.DialogStack, DialogChainLocked());
        }

        if (write)
        {
            StartWriting();
        }

        return entry;
    }

    public IReadOnlyList<DialogStackSegment> DialogStack
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.dialogs.Select(entry => entry.Segment)];
            }
        }
    }

    public IReadOnlyList<DialogStackSegment> TakeArrivedDialogChain()
    {
        bool write;
        IReadOnlyList<DialogStackSegment> chain;
        lock (this.gate)
        {
            write = CatchUpLocked();
            chain = this.arrivalTaken ? [] : this.arrivedDialogChain;
            this.arrivedDialogChain = [];
            this.arrivalTaken = true;
        }

        if (write)
        {
            StartWriting();
        }

        return chain;
    }

    public void MarkInteractive()
    {
        lock (this.gate)
        {
            if (this.interactive || this.disposed)
            {
                return;
            }

            this.interactive = true;
        }

        StartWriting();
    }

    public Task WhenWrittenAsync()
    {
        lock (this.gate)
        {
            return this.writing ? this.idle.Task : Task.CompletedTask;
        }
    }

    internal void Write(UrlStateBinding binding)
    {
        bool write;
        lock (this.gate)
        {
            write = CatchUpLocked();

            if (!this.disposed && IsCurrentLocked(binding))
            {
                write |= QueueLocked(binding.Name, binding.Text);
            }
        }

        if (write)
        {
            StartWriting();
        }
    }

    internal void Unbind(UrlStateBinding binding)
    {
        bool write;
        lock (this.gate)
        {
            this.bindings.Remove(binding);
            write = CatchUpLocked();

            if (!this.disposed && IsCurrentLocked(binding))
            {
                write |= QueueLocked(binding.Name, null);
            }
        }

        if (write)
        {
            StartWriting();
        }
    }

    internal void Pop(DialogStackEntry entry)
    {
        bool write;
        lock (this.gate)
        {
            if (this.disposed || !this.dialogs.Remove(entry))
            {
                return;
            }

            write = CatchUpLocked();
            write |= QueueLocked(UrlStateKeys.DialogStack, DialogChainLocked());
        }

        if (write)
        {
            StartWriting();
        }
    }

    private bool IsCurrentLocked(UrlStateBinding binding) =>
        binding is { Superseded: false, Disposed: false } && binding.Page == this.page && binding.Navigation == this.navigationCount;

    private bool QueueLocked(string name, string? text)
    {
        if (this.values.GetValueOrDefault(name) == text)
        {
            return false;
        }

        if (text is null)
        {
            this.values.Remove(name);
        }
        else
        {
            this.values[name] = text;
        }

        this.pending[name] = text;
        return true;
    }

    private string? DialogChainLocked() => DialogStackSegment.FormatChain(this.dialogs.Select(entry => entry.Segment));

    private void StartWriting()
    {
        lock (this.gate)
        {
            if (this.writing || this.disposed || !this.interactive || this.pending.Count == 0)
            {
                return;
            }

            this.writing = true;
            this.idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = WriteQueuedAsync();
    }

    private async Task WriteQueuedAsync()
    {
        while (true)
        {
            string path;
            int navigation;
            Dictionary<string, string?> batch;
            TaskCompletionSource idle;
            lock (this.gate)
            {
                if (this.disposed || this.pending.Count == 0)
                {
                    this.writing = false;
                    idle = this.idle;
                    batch = [];
                    path = string.Empty;
                    navigation = 0;
                }
                else
                {
                    batch = new Dictionary<string, string?>(this.pending, StringComparer.Ordinal);
                    this.pending.Clear();
                    path = this.path;
                    navigation = this.navigationCount;
                    idle = null!;
                }
            }

            if (batch.Count == 0)
            {
                idle.TrySetResult();
                return;
            }

            try
            {
                await this.writer.ApplyAsync(path, batch);
            }
            catch (Exception exception)
            {
                this.logger.LogWarning(exception, "The page's state could not be written to the address; it goes again with the next write.");
                lock (this.gate)
                {
                    if (!this.disposed && navigation == this.navigationCount)
                    {
                        foreach (var (name, text) in batch)
                        {
                            this.pending.TryAdd(name, text);
                        }
                    }

                    this.writing = false;
                    idle = this.idle;
                }

                idle.TrySetResult();
                return;
            }
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        bool write;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            if (string.Equals(this.caughtUpUri, e.Location, StringComparison.Ordinal))
            {
                this.caughtUpUri = null;
                return;
            }

            this.caughtUpUri = null;
            write = ReseedLocked(e.Location, navigated: true);
        }

        if (write)
        {
            StartWriting();
        }
    }

    private bool CatchUpLocked()
    {
        var current = this.navigation.Uri;
        if (this.disposed || string.Equals(current, this.seededUri, StringComparison.Ordinal))
        {
            return false;
        }

        this.caughtUpUri = current;
        return ReseedLocked(current, navigated: true);
    }

    private bool ReseedLocked(string uri, bool navigated)
    {
        var previousPath = this.path;
        this.seededUri = uri;
        this.path = PathOf(uri);
        this.values.Clear();
        this.pending.Clear();
        if (navigated)
        {
            this.navigationCount++;
        }

        foreach (var (name, values) in QueryHelpers.ParseQuery(QueryOf(uri)))
        {
            if (values.Count > 0 && values[^1] is { Length: > 0 } value)
            {
                this.values[name] = value;
            }
        }

        var arrived = DialogStackSegment.ParseChain(this.values.GetValueOrDefault(UrlStateKeys.DialogStack));
        if (arrived.Count > 0 || !string.Equals(previousPath, this.path, StringComparison.OrdinalIgnoreCase))
        {
            this.arrivedDialogChain = arrived;
        }

        return QueueLocked(UrlStateKeys.DialogStack, DialogChainLocked());
    }

    private static string PathOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : string.Empty;

    private static string QueryOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute.Query : string.Empty;

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }

    public void Dispose()
    {
        TaskCompletionSource idle;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.pending.Clear();
            this.bindings.Clear();
            this.dialogs.Clear();
            idle = this.idle;
        }

        this.navigation.LocationChanged -= OnLocationChanged;
        idle.TrySetResult();
    }
}

public abstract class UrlStateBinding : IDisposable
{
    private protected UrlStateBinding(string name, object page, int navigation)
    {
        Name = name;
        Page = page;
        Navigation = navigation;
    }

    public string Name { get; }

    internal object Page { get; }

    internal int Navigation { get; set; }

    internal bool Superseded { get; set; }

    internal bool Exclusive { get; init; }

    internal bool Disposed { get; private protected set; }

    internal abstract string? Text { get; }

    internal abstract bool AdoptLocked(string? text);

    internal abstract void RaiseChanged();

    public abstract void Dispose();
}

public sealed class UrlStateValue<T> : UrlStateBinding
{
    private readonly UrlStateService owner;
    private T value;

    internal UrlStateValue(UrlStateService owner, UrlStateKey<T> key, T value, object page, int navigation)
        : base(key.Name, page, navigation)
    {
        this.owner = owner;
        Key = key;
        this.value = value;
    }

    public UrlStateKey<T> Key { get; }

    public T Value
    {
        get => this.value;
        set
        {
            this.value = value;
            this.owner.Write(this);
        }
    }

    public event Action? Changed;

    internal override string? Text => Key.Format(this.value);

    internal override bool AdoptLocked(string? text)
    {
        var value = Key.Parse(text);
        if (EqualityComparer<T>.Default.Equals(value, this.value))
        {
            return false;
        }

        this.value = value;
        return true;
    }

    internal override void RaiseChanged() => Changed?.Invoke();

    public override void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        this.owner.Unbind(this);
        Disposed = true;
        Changed = null;
    }
}

internal sealed class DialogStackEntry : IDisposable
{
    private int disposed;
    private readonly UrlStateService owner;

    public DialogStackEntry(UrlStateService owner, DialogStackSegment segment)
    {
        this.owner = owner;
        Segment = segment;
    }

    public DialogStackSegment Segment { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 0)
        {
            this.owner.Pop(this);
        }
    }
}
