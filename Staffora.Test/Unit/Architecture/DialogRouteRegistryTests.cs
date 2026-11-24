using System.Reflection;
using System.Runtime.CompilerServices;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Test.Unit.Architecture.DialogScanSamples;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Unit.Architecture;

public class DialogRouteRegistryTests
{
    private const string SamplesNamespace = "Staffora.Test.Unit.Architecture.DialogScanSamples";

    [Fact]
    public void Every_declared_token_is_registered_and_every_registered_opener_is_declared()
    {
        var types = RealTypes();
        var declared = DialogRouteRegistry.ContextsIn(types).ToDictionary(context => context, context => DialogRouteAttribute.TokenOf(context));

        Assert.Empty(DialogRouteRegistry.ProblemsIn(types));
        Assert.DoesNotContain(declared, pair => pair.Value is null);
        var registry = DialogRouteRegistry.FromTypes(types);

        Assert.Equal(declared.Values.Order(StringComparer.Ordinal), registry.Routes.Select(route => route.Token).Order(StringComparer.Ordinal));
        foreach (var (context, token) in declared)
        {
            Assert.Equal(context, registry.Find(token!)!.ContextType);
        }

        foreach (var route in registry.Routes)
        {
            Assert.Equal(route.Token, DialogRouteAttribute.TokenOf(route.ContextType));
            Assert.Equal(route.ContextType, DialogRouteRegistry.ContextOfBody(route.BodyType));
            Assert.Equal(route.BodyType, route.Opener.DeclaringType);
            Assert.True(DialogRouteRegistry.IsOpenerShaped(route.Opener));
        }

        var openers = types
            .Where(type => DialogRouteRegistry.ContextOfBody(type) is not null)
            .SelectMany(body => body.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(DialogRouteRegistry.IsOpenerShaped))
            .ToHashSet();
        Assert.Equal(openers, registry.Routes.Select(route => route.Opener).ToHashSet());

        Assert.Equal(TestParentDialogContext.Token, registry.Find(TestParentDialogContext.Token)!.Token);
        Assert.Equal(typeof(TestChildDialog), registry.Find(TestChildDialogContext.Token)!.BodyType);
        Assert.Equal(typeof(TestParentDialog).GetMethod(nameof(TestParentDialog.OpenAsync)), registry.Find(TestParentDialogContext.Token)!.Opener);
    }

    [Fact]
    public void The_app_registers_exactly_its_own_dialogs_and_none_of_the_tests()
    {
        var app = DialogRouteRegistry.FromAssemblies(typeof(StateEntityDialogContext).Assembly);
        var declared = DialogRouteRegistry.ContextsIn(typeof(StateEntityDialogContext).Assembly.GetTypes()).Select(DialogRouteAttribute.TokenOf).ToList();

        Assert.Equal(declared.Order(StringComparer.Ordinal), app.Routes.Select(route => route.Token).Order(StringComparer.Ordinal));
        Assert.All(app.Routes, route => Assert.Equal(typeof(StateEntityDialogContext).Assembly, route.ContextType.Assembly));
        Assert.Null(app.Find(TestParentDialogContext.Token));
    }

    [Fact]
    public void A_contexts_DialogToken_is_its_attributes_token_and_no_context_can_write_another()
    {
        var property = typeof(StateEntityDialogContext).GetProperty(nameof(StateEntityDialogContext.DialogToken))!;
        Assert.False(property.GetMethod!.IsVirtual);
        Assert.False(property.GetMethod.IsAbstract);
        var hiding = RealTypes().Concat(typeof(DialogRouteRegistryTests).Assembly.GetTypes())
            .Where(type => type != typeof(StateEntityDialogContext) && typeof(StateEntityDialogContext).IsAssignableFrom(type))
            .Where(type => type.GetMember(nameof(StateEntityDialogContext.DialogToken), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length > 0)
            .Select(type => type.FullName);
        Assert.Empty(hiding);

        foreach (var context in DialogRouteRegistry.ContextsIn(RealTypes()))
        {
            var instance = (StateEntityDialogContext)RuntimeHelpers.GetUninitializedObject(context);
            Assert.Equal(DialogRouteAttribute.TokenOf(context), instance.DialogToken);
        }

        var unrouted = (StateEntityDialogContext)RuntimeHelpers.GetUninitializedObject(typeof(UnroutedSampleContext));
        Assert.Contains("declares no [DialogRoute]", Assert.Throws<InvalidOperationException>(() => unrouted.DialogToken).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declaration_as_the_recipe_writes_it_is_registered_with_its_opener()
    {
        var registry = DialogRouteRegistry.FromTypes([typeof(RoutedSampleContext), typeof(RoutedSampleBody)]);

        var route = Assert.Single(registry.Routes);
        Assert.Equal("sampleroute", route.Token);
        Assert.Equal(typeof(RoutedSampleBody).GetMethod(nameof(RoutedSampleBody.OpenAsync)), route.Opener);
        Assert.Equal(typeof(MatchingSampleService), route.ServiceType);
        Assert.Null(registry.Find("samplenone"));
    }

    [Theory]
    [InlineData(typeof(UnroutedSampleContext), null, "declares no [DialogRoute]")]
    [InlineData(typeof(BadTokenSampleContext), null, "declares a token the dialog stack refuses")]
    [InlineData(typeof(BodilessSampleContext), null, "has no body")]
    [InlineData(typeof(OpenerlessSampleContext), typeof(OpenerlessSampleBody), "has no id-only opener")]
    [InlineData(typeof(WrongOpenerSampleContext), typeof(WrongOpenerSampleBody), "has no id-only opener")]
    [InlineData(typeof(TwoOpenersSampleContext), typeof(TwoOpenersSampleBody), "has 2 id-only openers")]
    [InlineData(typeof(ValueTaskOpenerSampleContext), typeof(ValueTaskOpenerSampleBody), "has no id-only opener")]
    [InlineData(typeof(OtherResultOpenerSampleContext), typeof(OtherResultOpenerSampleBody), "has no id-only opener")]
    [InlineData(typeof(InterfaceServiceSampleContext), typeof(InterfaceServiceSampleBody), "has no id-only opener")]
    [InlineData(typeof(AbstractServiceSampleContext), typeof(AbstractServiceSampleBody), "has no id-only opener")]
    [InlineData(typeof(DerivedUndeclaredSampleContext), null, "declares no [DialogRoute]")]
    public void A_broken_declaration_is_refused_with_its_reason(Type context, Type? body, string reason)
    {
        Type[] types = body is null ? [context] : [context, body];

        var problem = Assert.Single(DialogRouteRegistry.ProblemsIn(types));
        Assert.Contains(context.Name, problem, StringComparison.Ordinal);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
        Assert.Contains(reason, Assert.Throws<DialogRouteException>(() => DialogRouteRegistry.FromTypes(types)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_context_derived_from_a_declared_one_names_itself_and_does_not_inherit_the_token()
    {
        Assert.Equal("sampledeclaredbase", DialogRouteAttribute.TokenOf(typeof(DeclaredBaseSampleContext)));
        Assert.Null(DialogRouteAttribute.TokenOf(typeof(DerivedUndeclaredSampleContext)));
        var route = Assert.Single(DialogRouteRegistry.FromTypes([typeof(DeclaredBaseSampleContext), typeof(DeclaredBaseSampleBody)]).Routes);
        Assert.Equal(typeof(DeclaredBaseSampleContext), route.ContextType);

        var problem = Assert.Single(DialogRouteRegistry.ProblemsIn([typeof(DeclaredBaseSampleContext), typeof(DeclaredBaseSampleBody), typeof(DerivedUndeclaredSampleContext)]));
        Assert.Contains(nameof(DerivedUndeclaredSampleContext), problem, StringComparison.Ordinal);
        Assert.Contains("declares no [DialogRoute]", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_open_generic_component_is_no_body_of_a_context()
    {
        var route = Assert.Single(DialogRouteRegistry.FromTypes([typeof(RoutedSampleContext), typeof(RoutedSampleBody), typeof(OpenGenericSampleBody<>)]).Routes);

        Assert.Equal(typeof(RoutedSampleBody), route.BodyType);
        Assert.Equal(typeof(RoutedSampleBody).GetMethod(nameof(RoutedSampleBody.OpenAsync)), route.Opener);
    }

    [Fact]
    public void Two_contexts_declaring_one_token_are_refused()
    {
        var problem = Assert.Single(DialogRouteRegistry.ProblemsIn([typeof(DuplicateTokenSampleContext), typeof(RoutedSampleContext), typeof(RoutedSampleBody)]));

        Assert.Contains("\"sampleroute\"", problem, StringComparison.Ordinal);
        Assert.Contains("declared more than once", problem, StringComparison.Ordinal);
        Assert.Contains(nameof(RoutedSampleContext), problem, StringComparison.Ordinal);
        Assert.Contains(nameof(DuplicateTokenSampleContext), problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_real_token_is_one_the_dialog_stack_accepts_and_no_two_share_one()
    {
        var tokens = DialogRouteRegistry.ContextsIn(RealTypes()).Select(DialogRouteAttribute.TokenOf).ToList();

        Assert.All(tokens, token => Assert.True(Staffora.Blazor.Helper.UrlState.DialogStackSegment.IsValidToken(token)));
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    private static List<Type> RealTypes() =>
        [.. new[] { typeof(StateEntityDialogContext).Assembly, typeof(DialogRouteRegistryTests).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace != SamplesNamespace)];
}
