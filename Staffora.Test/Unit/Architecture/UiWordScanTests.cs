using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.RegularExpressions;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class UiWordScanTests
{
    private static readonly (string Phrase, string Why)[] allowedPhrases = [];

    private static readonly Assembly[] productAssemblies =
    [
        typeof(Tenant).Assembly,
        typeof(ApplicationDbContext).Assembly,
        typeof(JobService).Assembly,
        typeof(Staffora.Blazor.Helper.DialogServiceExtensions).Assembly,
    ];

    internal static IReadOnlyList<Assembly> ProductAssemblies => productAssemblies;

    private static readonly string[] labelAttributes = ["Title", "Label", "Text", "EmptyTitle", "DefaultTitle", "CreateTitle", "AddLabel", "Legend"];

    [Fact]
    public void No_Razor_markup_names_an_old_word()
    {
        var files = RazorFiles().ToList();
        var offences = files
            .SelectMany(file => RazorOffences(Relative(file), File.ReadAllText(file)))
            .ToList();

        Assert.NotEmpty(files);
        Assert.Empty(offences);
    }

    [Fact]
    public void No_Razor_label_says_Need()
    {
        var offences = RazorFiles()
            .SelectMany(file => RazorLabelOffences(Relative(file), File.ReadAllText(file)))
            .ToList();

        Assert.Empty(offences);
    }

    [Fact]
    public void No_notice_of_the_message_catalogue_names_an_old_word()
    {
        var texts = MessageTypes.All
            .SelectMany(type => new[] { ($"{type.Key} name", type.Name), ($"{type.Key} key", type.Key) }
                .Concat(type.Placeholders.Select(placeholder => ($"{type.Key} placeholder", placeholder)))
                .Concat(MessageCatalogue.Templates.TryGetValue(type, out var template)
                    ? [($"{type.Key} subject", template.Subject), ($"{type.Key} text", template.Text), ($"{type.Key} link", template.Link ?? string.Empty)]
                    : []));

        Assert.Empty(TextOffences(texts));
    }

    [Fact]
    public void No_display_or_description_attribute_names_an_old_word_or_says_Need()
    {
        var attributes = DisplayTexts().ToList();

        Assert.NotEmpty(attributes);
        Assert.Empty(TextOffences(attributes));
        Assert.Empty(LabelOffences(attributes.Where(entry => entry.Where.EndsWith(" name", StringComparison.Ordinal))));
    }

    [Fact]
    public void No_workflow_step_names_an_old_word_or_is_labelled_Need()
    {
        var steps = productAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(WorkflowRegistry.HasWorkflow)
            .SelectMany(type => WorkflowRegistry.Default.Get(type).Transitions.Select(step => (Type: type, Step: step)))
            .ToList();
        var texts = steps.SelectMany(entry => StepTexts($"{entry.Type.Name}.{entry.Step.Key}", entry.Step)).ToList();

        Assert.NotEmpty(texts);
        Assert.Empty(TextOffences(texts));
        Assert.Empty(LabelOffences(steps.Select(entry => ($"{entry.Type.Name}.{entry.Step.Key} label", entry.Step.Label ?? string.Empty))));
    }

    [Fact]
    public void Every_allow_listed_phrase_is_still_there()
    {
        var sources = RazorFiles().Select(File.ReadAllText).ToList();

        Assert.All(allowedPhrases, entry => Assert.Contains(sources, text => text.Contains(entry.Phrase, StringComparison.Ordinal)));
    }

    [Fact]
    public void The_scans_catch_each_old_word_in_markup_an_attribute_a_string_and_a_label_and_leave_comments()
    {
        const string sample = """
            @page "/staffing/courses"
            @* A course's dialog: comments are not the UI. *@
            <h2 class="dialog__teacher">Lessons</h2>
            <span data-term>@(count == 1 ? "occurrence" : "occurrences")</span>
            <ListColumn Title="Needs" />
            @code {
                // The teacher's own comment.
                private string Name => "Spring term";
            }
            """;

        Assert.Equal(
            ["sample.razor:1 courses", "sample.razor:3 teacher", "sample.razor:3 Lessons", "sample.razor:4 term", "sample.razor:8 term"],
            RazorOffences("sample.razor", sample));
        Assert.Equal(["sample.razor:5 Needs"], RazorLabelOffences("sample.razor", sample));
        Assert.Empty(RazorOffences("ok.razor", "<p>Jobs, slots and periods; a time on Monday; the staff member's work. Nothing needs you.</p>"));
        Assert.Equal(["A label: Course"], TextOffences([("A label", "Course")]));
        Assert.Equal(["A step's reason: lesson"], TextOffences([("A step's reason", "The timetable has a lesson")]));
        Assert.Empty(TextOffences([("A refusal", "Every slot needs a staff member for the period.")]));
        Assert.Equal(["Slots name: Needs"], LabelOffences([("Slots name", "Needs")]));
        Assert.Empty(LabelOffences([("A reason", "The job needs at least one slot")]));
    }

    internal static IEnumerable<string> RazorFiles() =>
        RepoFiles.CommittedTextFiles(".razor").Where(file => Relative(file).StartsWith("Staffora.Blazor/", StringComparison.Ordinal));

    internal static string Relative(string file) => Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/');

    private static List<string> RazorOffences(string path, string text) =>
        [.. WithoutComments(text).Split('\n')
            .Select((line, index) => (Line: Allowed(line), Number: index + 1))
            .SelectMany(line => OldWord().Matches(line.Line).Select(match => $"{path}:{line.Number} {match.Value}"))];

    private static List<string> RazorLabelOffences(string path, string text) =>
        [.. WithoutComments(text).Split('\n')
            .Select((line, index) => (Line: line, Number: index + 1))
            .SelectMany(line => LabelAttribute().Matches(line.Line)
                .Where(match => labelAttributes.Contains(match.Groups["name"].Value, StringComparer.Ordinal))
                .SelectMany(match => NeedLabel().Matches(match.Groups["value"].Value))
                .Select(match => $"{path}:{line.Number} {match.Value}"))];

    private static List<string> TextOffences(IEnumerable<(string Where, string Text)> texts) =>
        [.. texts.SelectMany(entry => OldWord().Matches(Allowed(entry.Text)).Select(match => $"{entry.Where}: {match.Value}"))];

    private static List<string> LabelOffences(IEnumerable<(string Where, string Text)> labels) =>
        [.. labels.SelectMany(entry => NeedLabel().Matches(entry.Text).Select(match => $"{entry.Where}: {match.Value}"))];

    private static string Allowed(string text) =>
        allowedPhrases.Aggregate(text, (rest, entry) => rest.Replace(entry.Phrase, string.Empty, StringComparison.Ordinal));

    internal static string WithoutComments(string text)
    {
        static string Blank(Match match) => new(match.Value.Where(character => character == '\n').ToArray());
        var withoutBlocks = BlockComment().Replace(text, Blank);
        return LineComment().Replace(withoutBlocks, match => match.Groups["lead"].Value);
    }

    internal static IEnumerable<(string Where, string Text)> DisplayTexts()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in productAssemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            foreach (var member in new MemberInfo[] { type }.Concat(type.GetMembers(all)))
            {
                var where = member == type ? type.Name : $"{type.Name}.{member.Name}";
                if (member.GetCustomAttribute<DisplayAttribute>(inherit: false) is { } display)
                {
                    foreach (var (part, text) in new[] { ("name", display.Name), ("short name", display.ShortName), ("description", display.Description), ("group", display.GroupName), ("prompt", display.Prompt) })
                    {
                        if (!string.IsNullOrEmpty(text))
                        {
                            yield return ($"{where} {part}", text);
                        }
                    }
                }

                if (member.GetCustomAttribute<DescriptionAttribute>(inherit: false) is { Description.Length: > 0 } description)
                {
                    yield return ($"{where} description", description.Description);
                }
            }
        }
    }

    internal static IEnumerable<(string Where, string Text)> StepTexts(string where, ITransition step) =>
        new[]
        {
            ($"{where} label", step.Label),
            ($"{where} description", step.Description),
            ($"{where} confirmation", step.ConfirmationMessage),
            ($"{where} prompt", step.CommentPrompt?.Title),
            ($"{where} prompt message", step.CommentPrompt?.Message),
            ($"{where} prompt placeholder", step.CommentPrompt?.Placeholder),
        }
        .Concat(step.ConditionDescriptions.Select(reason => ($"{where} condition", (string?)reason)))
        .Where(entry => !string.IsNullOrEmpty(entry.Item2))
        .Select(entry => (entry.Item1, entry.Item2!));

    [GeneratedRegex("(?<![A-Za-z0-9])(?:teachers?|courses?|lessons?|terms?)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldWord();

    [GeneratedRegex(@"\bNeeds?\b", RegexOptions.CultureInvariant)]
    private static partial Regex NeedLabel();

    [GeneratedRegex(@"(?<name>[A-Za-z]+)=""(?<value>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex LabelAttribute();

    [GeneratedRegex(@"@\*.*?\*@|<!--.*?-->|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"(?<lead>^|[\s;{}()])//.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();
}
