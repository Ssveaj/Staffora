using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Test.Unit.Data;

namespace Staffora.Test.Unit.Architecture;

public partial class DomainNameScanTests
{
    private static readonly Assembly[] productAssemblies =
    [
        typeof(Staffora.Core.Models.Result).Assembly,
        typeof(ApplicationDbContext).Assembly,
        typeof(JobService).Assembly,
        typeof(Staffora.Blazor.Helper.DialogServiceExtensions).Assembly,
    ];

    private static readonly Dictionary<string, string> allowList = new(StringComparer.Ordinal);

    [Fact]
    public void No_type_member_or_parameter_is_named_after_a_teacher_a_course_a_term_a_need_or_a_lesson()
    {
        var offences = productAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !IsCompilerGenerated(type))
            .SelectMany(TypeNames)
            .Where(name => OldWord(name.Name) && !allowList.ContainsKey(name.Name))
            .Select(name => $"{name.Where}: {name.Name}")
            .Distinct()
            .ToList();

        Assert.Empty(offences);
    }

    [Fact]
    public void No_table_column_key_index_or_constraint_is_named_after_a_teacher_a_course_a_term_a_need_or_a_lesson()
    {
        var offences = DatabaseNames(ModelUnderTest.Model)
            .Where(name => OldWord(name.Name) && !allowList.ContainsKey(name.Name))
            .Select(name => $"{name.Where}: {name.Name}")
            .Distinct()
            .ToList();

        Assert.Empty(offences);
    }

    [Fact]
    public void The_scan_reads_names_by_their_segments_and_catches_each_old_word()
    {
        Assert.True(OldWord("TeacherBoardService"));
        Assert.True(OldWord("ICourseGuard"));
        Assert.True(OldWord("termStart"));
        Assert.True(OldWord("CK_Courses_TermInOrder"));
        Assert.True(OldWord("teachers"));
        Assert.True(OldWord("BoardNeedLesson"));
        Assert.True(OldWord("remainingLessons"));
        Assert.False(OldWord("NotifyTenantChangedIfNeeded"));
        Assert.False(OldWord("Determine"));
        Assert.False(OldWord("Terminal"));
        Assert.False(OldWord("TeachingRequest"));
        Assert.False(OldWord("JobSlotId"));
        Assert.Equal(
            ["CourseId", "NeedId", "TeacherNeed", "lessons", "term"],
            TypeNames(typeof(DomainNameSample)).Concat(TypeNames(typeof(DomainNameSample.TeacherNeed))).Where(name => OldWord(name.Name)).Select(name => name.Name).Distinct().Order(StringComparer.Ordinal));
    }

    internal static bool OldWord(string name) =>
        name.Split('_').SelectMany(part => Segment().Matches(part).Select(match => match.Value))
            .Any(segment => segment.ToLowerInvariant() is "teacher" or "teachers" or "course" or "courses" or "term" or "terms" or "need" or "needs" or "lesson" or "lessons");

    private static IEnumerable<(string Where, string Name)> TypeNames(Type type)
    {
        const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var typeName = type.Name.Split('`')[0];
        yield return (type.FullName ?? type.Name, typeName);
        foreach (var member in type.GetMembers(declared).Where(member => member is not Type && !IsCompilerGenerated(member)))
        {
            yield return ($"{type.FullName}.{member.Name}", member.Name);
            if (member is MethodBase method)
            {
                foreach (var parameter in method.GetParameters().Where(parameter => parameter.Name is not null))
                {
                    yield return ($"{type.FullName}.{member.Name}({parameter.Name})", parameter.Name!);
                }
            }
        }
    }

    private static IEnumerable<(string Where, string Name)> DatabaseNames(IModel model)
    {
        foreach (var entity in model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null)
            {
                continue;
            }

            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            yield return ("table", table);
            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName(store) is { } column)
                {
                    yield return ($"column of {table}", column);
                }
            }

            foreach (var key in entity.GetKeys())
            {
                if (key.GetName(store) is { } name)
                {
                    yield return ($"key of {table}", name);
                }
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                if (foreignKey.GetConstraintName(store, foreignKey.PrincipalKey.DeclaringEntityType.GetTableName() is { } principal ? StoreObjectIdentifier.Table(principal, foreignKey.PrincipalKey.DeclaringEntityType.GetSchema()) : store) is { } name)
                {
                    yield return ($"foreign key of {table}", name);
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                if (index.GetDatabaseName(store) is { } name)
                {
                    yield return ($"index of {table}", name);
                }
            }

            foreach (var check in entity.GetCheckConstraints())
            {
                if (check.Name is { } name)
                {
                    yield return ($"check constraint of {table}", name);
                }
            }
        }
    }

    private static bool IsCompilerGenerated(MemberInfo member) =>
        member.Name.Contains('<', StringComparison.Ordinal) || member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    [GeneratedRegex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();
}

internal sealed class DomainNameSample
{
    public Guid CourseId { get; set; }

    public Guid NeedId { get; set; }

    public static int Plan(int term, int lessons) => term + lessons;

    public sealed class TeacherNeed;
}
