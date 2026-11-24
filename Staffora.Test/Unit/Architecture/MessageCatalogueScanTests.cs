using System.Reflection;
using Staffora.Core.Messaging;
using Staffora.Data;
using Staffora.Services.Messaging;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public class MessageCatalogueScanTests
{
    private static readonly Assembly[] productAssemblies =
    [
        typeof(MessageType).Assembly,
        typeof(ApplicationDbContext).Assembly,
        typeof(NoticeService).Assembly,
        typeof(Staffora.Blazor.Helper.DialogServiceExtensions).Assembly,
        typeof(Staffora.E2ESetup.JourneyTenantUsers).Assembly,
    ];

    [Fact]
    public void Every_message_type_has_a_template_that_uses_only_its_own_placeholders()
    {
        var declared = DeclaredTypes(productAssemblies.SelectMany(TypesOf));

        Assert.NotEmpty(declared);
        Assert.Equal(declared.Count, declared.Select(type => type.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(declared.OrderBy(type => type.Key, StringComparer.Ordinal), MessageTypes.All.OrderBy(type => type.Key, StringComparer.Ordinal));
        Assert.Empty(CatalogueProblems(declared, MessageCatalogue.Templates));
    }

    [Fact]
    public void The_scan_catches_a_foreign_placeholder_a_misspelt_one_a_blank_text_a_link_out_of_the_app_and_a_type_without_a_template()
    {
        var type = new MessageType("sample-notice", MessageCategory.Account, "Sample", "Name");
        var other = new MessageType("sample-other", MessageCategory.Account, "Other", "Name");
        var good = new MessageTemplate("{{Name}} is here", "Hello {{Name}}.", "/me/overview");
        Assert.Empty(CatalogueProblems([type], new Dictionary<MessageType, MessageTemplate> { [type] = good }));

        Assert.Contains("{{Email}}", Assert.Single(CatalogueProblems([type], new Dictionary<MessageType, MessageTemplate> { [type] = good with { Text = "Hello {{Name}}, {{Email}}." } })), StringComparison.Ordinal);
        Assert.Contains("braces", Assert.Single(CatalogueProblems([type], new Dictionary<MessageType, MessageTemplate> { [type] = good with { Subject = "{{Name} is here" } })), StringComparison.Ordinal);
        Assert.Contains("blank", Assert.Single(CatalogueProblems([type], new Dictionary<MessageType, MessageTemplate> { [type] = good with { Text = " " } })), StringComparison.Ordinal);
        Assert.Contains("not a local address", Assert.Single(CatalogueProblems([type], new Dictionary<MessageType, MessageTemplate> { [type] = good with { Link = "//evil.example/{{Name}}" } })), StringComparison.Ordinal);
        Assert.Contains("not a local address", Assert.Single(CatalogueProblems([type], new Dictionary<MessageType, MessageTemplate> { [type] = good with { Link = "https://evil.example" } })), StringComparison.Ordinal);
        Assert.Contains("has no template", Assert.Single(CatalogueProblems([type, other], new Dictionary<MessageType, MessageTemplate> { [type] = good })), StringComparison.Ordinal);
        Assert.Contains("declares no such type", Assert.Single(CatalogueProblems([type], new Dictionary<MessageType, MessageTemplate> { [type] = good, [other] = good })), StringComparison.Ordinal);

        Assert.Contains(DeclaredTypes([typeof(SampleDeclarations)]), found => found.Key == SampleDeclarations.Stray.Key);
    }

    [Fact]
    public void No_notice_is_rendered_as_markup()
    {
        string[] files =
        [
            "Staffora.Blazor/Components/Account/NotificationBell.razor",
            "Staffora.Blazor/Components/Common/Dialogs/StateEntity/StateHistoryStepView.razor",
        ];

        Assert.All(files, file => Assert.DoesNotContain("MarkupString", File.ReadAllText(Path.Combine(RepoFiles.Root, file)), StringComparison.Ordinal));
    }

    private static List<string> CatalogueProblems(IReadOnlyCollection<MessageType> declared, IReadOnlyDictionary<MessageType, MessageTemplate> templates)
    {
        var problems = new List<string>();
        problems.AddRange(declared.Where(type => !templates.ContainsKey(type)).Select(type => $"\"{type.Key}\" has no template in the catalogue."));
        problems.AddRange(templates.Keys.Where(type => !declared.Contains(type)).Select(type => $"The catalogue holds a template of \"{type.Key}\", but the product declares no such type."));
        problems.AddRange(templates.Where(entry => declared.Contains(entry.Key)).SelectMany(entry => MessageCatalogue.ProblemsOf(entry.Key, entry.Value)));
        return problems;
    }

    private static List<MessageType> DeclaredTypes(IEnumerable<Type> types) =>
        [.. types
            .Where(type => !type.ContainsGenericParameters)
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(field => field.FieldType == typeof(MessageType))
                .Select(field => (MessageType?)field.GetValue(null))
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(property => property.PropertyType == typeof(MessageType) && property.GetIndexParameters().Length == 0)
                    .Select(property => (MessageType?)property.GetValue(null))))
            .OfType<MessageType>()
            .Distinct()];

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

    private static class SampleDeclarations
    {
        public static readonly MessageType Stray = new("sample-stray", MessageCategory.Account, "Stray");
    }
}
