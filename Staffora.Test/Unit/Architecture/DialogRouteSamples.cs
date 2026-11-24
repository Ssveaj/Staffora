using Microsoft.AspNetCore.Components;
using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Unit.Architecture.DialogScanSamples;

[DialogRoute("sampleroute")]
public sealed class RoutedSampleContext : InheritedSampleContextBase
{
    public RoutedSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class RoutedSampleBody : ComponentBase, IStateEntityDialog<RoutedSampleContext>
{
    [Parameter] public RoutedSampleContext Context { get; set; } = null!;

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, MatchingSampleService service, Guid id, StateEntityDialogOptions? options = null) =>
        dialogService.OpenStateEntityDialogAsync<RoutedSampleBody, RoutedSampleContext>(new RoutedSampleContext(service, id), options);

    public static Task<StateEntityDialogResult> OpenCreateAsync(DialogService dialogService, MatchingSampleService service) =>
        dialogService.OpenStateEntityDialogAsync<RoutedSampleBody, RoutedSampleContext>(new RoutedSampleContext(service, null));
}

public sealed class UnroutedSampleContext : InheritedSampleContextBase
{
    public UnroutedSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

[DialogRoute("Bad-Token")]
public sealed class BadTokenSampleContext : InheritedSampleContextBase
{
    public BadTokenSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

[DialogRoute("sampleroute")]
public sealed class DuplicateTokenSampleContext : InheritedSampleContextBase
{
    public DuplicateTokenSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

[DialogRoute("samplenobody")]
public sealed class BodilessSampleContext : InheritedSampleContextBase
{
    public BodilessSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

[DialogRoute("samplenoopener")]
public sealed class OpenerlessSampleContext : InheritedSampleContextBase
{
    public OpenerlessSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class OpenerlessSampleBody : ComponentBase, IStateEntityDialog<OpenerlessSampleContext>
{
    [Parameter] public OpenerlessSampleContext Context { get; set; } = null!;
}

[DialogRoute("samplewrongopener")]
public sealed class WrongOpenerSampleContext : InheritedSampleContextBase
{
    public WrongOpenerSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class WrongOpenerSampleBody : ComponentBase, IStateEntityDialog<WrongOpenerSampleContext>
{
    [Parameter] public WrongOpenerSampleContext Context { get; set; } = null!;

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, MatchingSampleService service, Guid id) =>
        dialogService.OpenStateEntityDialogAsync<WrongOpenerSampleBody, WrongOpenerSampleContext>(new WrongOpenerSampleContext(service, id));
}

[DialogRoute("sampletwoopeners")]
public sealed class TwoOpenersSampleContext : InheritedSampleContextBase
{
    public TwoOpenersSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class TwoOpenersSampleBody : ComponentBase, IStateEntityDialog<TwoOpenersSampleContext>
{
    [Parameter] public TwoOpenersSampleContext Context { get; set; } = null!;

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, MatchingSampleService service, Guid id, StateEntityDialogOptions? options = null) =>
        dialogService.OpenStateEntityDialogAsync<TwoOpenersSampleBody, TwoOpenersSampleContext>(new TwoOpenersSampleContext(service, id), options);

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, MismatchedSampleService service, Guid id, StateEntityDialogOptions? options = null) =>
        Task.FromResult(new StateEntityDialogResult(false, false, id));
}

[DialogRoute("samplevaluetask")]
public sealed class ValueTaskOpenerSampleContext : InheritedSampleContextBase
{
    public ValueTaskOpenerSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class ValueTaskOpenerSampleBody : ComponentBase, IStateEntityDialog<ValueTaskOpenerSampleContext>
{
    [Parameter] public ValueTaskOpenerSampleContext Context { get; set; } = null!;

    public static ValueTask<StateEntityDialogResult> OpenAsync(DialogService dialogService, MatchingSampleService service, Guid id, StateEntityDialogOptions? options = null) =>
        new(dialogService.OpenStateEntityDialogAsync<ValueTaskOpenerSampleBody, ValueTaskOpenerSampleContext>(new ValueTaskOpenerSampleContext(service, id), options));
}

[DialogRoute("sampleotherresult")]
public sealed class OtherResultOpenerSampleContext : InheritedSampleContextBase
{
    public OtherResultOpenerSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class OtherResultOpenerSampleBody : ComponentBase, IStateEntityDialog<OtherResultOpenerSampleContext>
{
    [Parameter] public OtherResultOpenerSampleContext Context { get; set; } = null!;

    public static async Task<bool> OpenAsync(DialogService dialogService, MatchingSampleService service, Guid id, StateEntityDialogOptions? options = null) =>
        (await dialogService.OpenStateEntityDialogAsync<OtherResultOpenerSampleBody, OtherResultOpenerSampleContext>(new OtherResultOpenerSampleContext(service, id), options)).Changed;
}

public interface ISampleDialogService;

public abstract class AbstractSampleDialogService;

[DialogRoute("sampleinterfaceservice")]
public sealed class InterfaceServiceSampleContext : InheritedSampleContextBase
{
    public InterfaceServiceSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class InterfaceServiceSampleBody : ComponentBase, IStateEntityDialog<InterfaceServiceSampleContext>
{
    [Parameter] public InterfaceServiceSampleContext Context { get; set; } = null!;

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, ISampleDialogService service, Guid id, StateEntityDialogOptions? options = null) =>
        Task.FromResult(new StateEntityDialogResult(false, false, id));
}

[DialogRoute("sampleabstractservice")]
public sealed class AbstractServiceSampleContext : InheritedSampleContextBase
{
    public AbstractServiceSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class AbstractServiceSampleBody : ComponentBase, IStateEntityDialog<AbstractServiceSampleContext>
{
    [Parameter] public AbstractServiceSampleContext Context { get; set; } = null!;

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, AbstractSampleDialogService service, Guid id, StateEntityDialogOptions? options = null) =>
        Task.FromResult(new StateEntityDialogResult(false, false, id));
}

[DialogRoute("sampledeclaredbase")]
public class DeclaredBaseSampleContext : InheritedSampleContextBase
{
    public DeclaredBaseSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class DeclaredBaseSampleBody : ComponentBase, IStateEntityDialog<DeclaredBaseSampleContext>
{
    [Parameter] public DeclaredBaseSampleContext Context { get; set; } = null!;

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, MatchingSampleService service, Guid id, StateEntityDialogOptions? options = null) =>
        dialogService.OpenStateEntityDialogAsync<DeclaredBaseSampleBody, DeclaredBaseSampleContext>(new DeclaredBaseSampleContext(service, id), options);
}

public sealed class DerivedUndeclaredSampleContext : DeclaredBaseSampleContext
{
    public DerivedUndeclaredSampleContext(MatchingSampleService service, Guid? id) : base(service, id)
    {
    }
}

public sealed class OpenGenericSampleBody<TItem> : ComponentBase, IStateEntityDialog<RoutedSampleContext>
{
    [Parameter] public RoutedSampleContext Context { get; set; } = null!;

    [Parameter] public TItem? Item { get; set; }

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, MatchingSampleService service, Guid id, StateEntityDialogOptions? options = null) =>
        dialogService.OpenStateEntityDialogAsync<RoutedSampleBody, RoutedSampleContext>(new RoutedSampleContext(service, id), options);
}

public sealed class OptionsDroppingSampleBody : ComponentBase, IStateEntityDialog<TestParentDialogContext>
{
    [Parameter] public TestParentDialogContext Context { get; set; } = null!;

    public static Task<StateEntityDialogResult> OpenAsync(DialogService dialogService, TestParentDialogService service, Guid id, StateEntityDialogOptions? options = null) =>
        TestParentDialog.OpenAsync(dialogService, service, id);
}
