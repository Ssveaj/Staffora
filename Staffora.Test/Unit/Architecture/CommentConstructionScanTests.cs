using System.Reflection;
using Staffora.Core.Entities.Comments;
using Staffora.Data;
using Staffora.Services.Comments;

namespace Staffora.Test.Unit.Architecture;

public class CommentConstructionScanTests
{
    private static readonly Assembly[] productAssemblies =
    [
        typeof(Comment).Assembly,
        typeof(ApplicationDbContext).Assembly,
        typeof(CommentService).Assembly,
        typeof(Staffora.Blazor.Helper.DialogServiceExtensions).Assembly,
        typeof(Staffora.E2ESetup.JourneyTenantUsers).Assembly,
    ];

    [Fact]
    public void Only_the_thread_writer_constructs_a_comment_or_a_thread()
    {
        var offenders = productAssemblies.SelectMany(assembly => assembly.GetTypes()).SelectMany(Constructions).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scan_catches_a_comment_and_a_thread_built_elsewhere_and_passes_the_writer()
    {
        Assert.Empty(Constructions(typeof(CommentThreadWriter)));

        Assert.Equal(["Comment", "CommentContainer"], Constructed(typeof(CommentThreadWriter)).Select(type => type.Name).Distinct().Order(StringComparer.Ordinal));

        Assert.Equal(
            [
                "CommentForgerySample.Forge builds a Comment outside CommentThreadWriter",
                "CommentForgerySample.Forge builds a CommentContainer outside CommentThreadWriter",
                "CommentForgerySample.ForgeLaterAsync builds a Comment outside CommentThreadWriter",
            ],
            Constructions(typeof(CommentForgerySample)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_commented_entity_of_the_product_is_a_state_entity_so_its_delete_meets_the_blocker()
    {
        var loose = productAssemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ICommentedEntity).IsAssignableFrom(type) && !typeof(Staffora.Core.Workflow.IStateEntity).IsAssignableFrom(type))
            .Select(type => type.Name);

        Assert.Empty(loose);
    }

    private static IEnumerable<string> Constructions(Type type)
    {
        if (IsWriter(type))
        {
            yield break;
        }

        foreach (var (method, built) in Builds(type))
        {
            yield return $"{Owner(method).Name}.{OwnerMethodName(method)} builds a {built.Name} outside {nameof(CommentThreadWriter)}";
        }
    }

    private static IEnumerable<Type> Constructed(Type type) => Builds(type).Select(build => build.Built);

    private static IEnumerable<(MethodBase Method, Type Built)> Builds(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        IEnumerable<MethodBase> bodies = [.. type.GetMethods(all), .. type.GetConstructors(all)];
        foreach (var method in bodies.Where(method => method.GetMethodBody() is not null))
        {
            foreach (var constructor in IlScan.DirectReferences(method).OfType<ConstructorInfo>())
            {
                if (constructor.DeclaringType is { } built && (typeof(Comment).IsAssignableFrom(built) || typeof(CommentContainer).IsAssignableFrom(built)))
                {
                    yield return (method, built);
                }
            }
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var build in Builds(nested))
            {
                yield return build;
            }
        }
    }

    private static bool IsWriter(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current == typeof(CommentThreadWriter))
            {
                return true;
            }
        }

        return false;
    }

    private static Type Owner(MethodBase method)
    {
        var type = method.DeclaringType!;
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
    }

    private static string OwnerMethodName(MethodBase method)
    {
        var name = method.DeclaringType!.Name;
        return method.DeclaringType.DeclaringType is not null && name.StartsWith('<') && name.IndexOf('>') is > 1 and var end
            ? name[1..end]
            : method.Name;
    }
}

internal static class CommentForgerySample
{
    public static (Comment, CommentContainer) Forge() =>
        (new Comment { Text = "Forged." }, new CommentContainer { EntityType = "Job" });

    public static async Task<Comment> ForgeLaterAsync()
    {
        await Task.Yield();
        return new Comment { Text = "Forged later." };
    }
}
