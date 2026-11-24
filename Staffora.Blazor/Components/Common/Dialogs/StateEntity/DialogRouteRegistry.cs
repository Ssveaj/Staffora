using System.Reflection;
using Radzen;
using Staffora.Blazor.Helper;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public sealed class DialogRouteRegistry
{
    public const string OpenerName = "OpenAsync";

    private readonly Dictionary<string, DialogRoute> routes;

    private DialogRouteRegistry(IEnumerable<DialogRoute> routes) =>
        this.routes = routes.ToDictionary(route => route.Token, StringComparer.Ordinal);

    public IReadOnlyCollection<DialogRoute> Routes => this.routes.Values;

    public DialogRoute? Find(string token) => this.routes.GetValueOrDefault(token);

    public static DialogRouteRegistry FromAssemblies(params Assembly[] assemblies) =>
        FromTypes(assemblies.SelectMany(TypesOf));

    public static DialogRouteRegistry FromTypes(IEnumerable<Type> types)
    {
        var (routes, problems) = Discover(types);
        return problems.Count > 0 ? throw new DialogRouteException(problems) : new DialogRouteRegistry(routes);
    }

    public static IReadOnlyList<string> ProblemsIn(IEnumerable<Type> types) => Discover(types).Problems;

    public static bool IsOpenerShaped(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var parameters = method.GetParameters();
        return method is { IsStatic: true, IsPublic: true, Name: OpenerName, IsGenericMethodDefinition: false }
            && method.ReturnType == typeof(Task<StateEntityDialogResult>)
            && parameters.Length == 4
            && parameters[0].ParameterType == typeof(DialogService)
            && parameters[1].ParameterType is { IsClass: true, IsAbstract: false } service && service != typeof(DialogService) && service != typeof(string)
            && parameters[2].ParameterType == typeof(Guid)
            && parameters[3].ParameterType == typeof(StateEntityDialogOptions);
    }

    public static IEnumerable<Type> ContextsIn(IEnumerable<Type> types) =>
        types.Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && typeof(StateEntityDialogContext).IsAssignableFrom(type));

    public static Type? ContextOfBody(Type body) =>
        body.GetInterfaces().FirstOrDefault(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IStateEntityDialog<>))?.GetGenericArguments()[0];

    private static (List<DialogRoute> Routes, List<string> Problems) Discover(IEnumerable<Type> types)
    {
        var all = types.Distinct().ToList();
        var problems = new List<string>();
        var routes = new List<DialogRoute>();
        var bodies = all.Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && ContextOfBody(type) is not null).ToList();
        var declared = new List<(Type Context, string Token)>();
        foreach (var context in ContextsIn(all).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            try
            {
                if (DialogRouteAttribute.TokenOf(context) is { } token)
                {
                    declared.Add((context, token));
                }
                else
                {
                    problems.Add($"{context.FullName} declares no [DialogRoute]: every dialog context names the token a link reopens it by.");
                }
            }
            catch (ArgumentException exception)
            {
                problems.Add($"{context.FullName} declares a token the dialog stack refuses: {exception.Message}");
            }
        }

        foreach (var group in declared.GroupBy(pair => pair.Token, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                problems.Add($"The token \"{group.Key}\" is declared more than once, by {string.Join(" and ", group.Select(pair => pair.Context.FullName))}: a token names one dialog, so neither is registered.");
                continue;
            }

            var (context, token) = group.Single();
            var ownBodies = bodies.Where(body => ContextOfBody(body) == context).ToList();
            if (ownBodies.Count != 1)
            {
                problems.Add(ownBodies.Count == 0
                    ? $"{context.FullName} (\"{token}\") has no body: no component implements IStateEntityDialog<{context.Name}>."
                    : $"{context.FullName} (\"{token}\") has {ownBodies.Count} bodies ({string.Join(", ", ownBodies.Select(body => body.Name))}); a context has one.");
                continue;
            }

            var body = ownBodies[0];
            var openers = body.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(IsOpenerShaped).ToList();
            if (openers.Count != 1)
            {
                problems.Add(openers.Count == 0
                    ? $"{body.FullName}, the body of {context.Name} (\"{token}\"), has no id-only opener: declare public static Task<StateEntityDialogResult> {OpenerName}(DialogService dialogService, TService service, Guid id, StateEntityDialogOptions? options = null)."
                    : $"{body.FullName}, the body of {context.Name} (\"{token}\"), has {openers.Count} id-only openers; a dialog has one.");
                continue;
            }

            routes.Add(new DialogRoute(token, context, body, openers[0]));
        }

        return (routes, problems);
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }
}

public sealed class DialogRoute
{
    internal DialogRoute(string token, Type contextType, Type bodyType, MethodInfo opener)
    {
        Token = token;
        ContextType = contextType;
        BodyType = bodyType;
        Opener = opener;
        ServiceType = opener.GetParameters()[1].ParameterType;
    }

    public string Token { get; }

    public Type ContextType { get; }

    public Type BodyType { get; }

    public MethodInfo Opener { get; }

    public Type ServiceType { get; }

    internal Task<StateEntityDialogResult> InvokeOpenerAsync(DialogService dialogService, IServiceProvider services, Guid id, StateEntityDialogOptions options)
    {
        var service = services.GetService(ServiceType) ?? throw new InvalidOperationException($"The {Token} dialog's opener takes a {ServiceType.Name}, which the circuit does not provide.");
        try
        {
            return (Task<StateEntityDialogResult>)Opener.Invoke(null, [dialogService, service, id, options])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

public sealed class DialogRouteException : InvalidOperationException
{
    public DialogRouteException(IReadOnlyList<string> problems) : base($"The dialog routes cannot be registered:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}")
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

public static class DialogRouteServiceCollectionExtensions
{
    public static IServiceCollection AddDialogRoutes(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.AddSingleton(DialogRouteRegistry.FromAssemblies(assemblies));
        return services;
    }
}
