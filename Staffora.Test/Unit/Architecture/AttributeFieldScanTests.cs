using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class AttributeFieldScanTests
{
    private const string Field = "Staffora.Blazor/Components/EntityAttributes/AttributeValueField.razor";

    private static readonly string[] dialogs =
    [
        "Staffora.Blazor/Components/Common/Dialogs/UserDialog.razor",
        "Staffora.Blazor/Components/Common/Dialogs/OrganizationUnitDialog.razor",
        "Staffora.Blazor/Components/Common/Dialogs/JobDialog.razor",
    ];

    [Fact]
    public void Only_the_value_field_reads_or_writes_a_custom_field_values_columns()
    {
        var offenders = Markup()
            .Where(source => source.Path != Field)
            .SelectMany(source => ValueColumn().Matches(WithoutComments(source.Text)).Select(match => $"{source.Path}: {match.Value}"));

        Assert.Empty(offenders);
        Assert.Matches(ValueColumn(), File.ReadAllText(Path.Combine(RepoFiles.Root, Field)));
    }

    [Fact]
    public void The_user_and_unit_dialogs_render_their_custom_fields_through_the_value_field_alone()
    {
        foreach (var dialog in dialogs)
        {
            var text = WithoutComments(File.ReadAllText(Path.Combine(RepoFiles.Root, dialog)));

            Assert.Matches(@"<(AttributeValueField|StateCustomFields)\b", text);
            Assert.DoesNotMatch(@"\bAttributeValueType\b", text);
            Assert.DoesNotMatch(@"Name=""@\(\$""(user|unit|job)-attribute-", text);
        }
    }

    [Fact]
    public void The_scan_catches_a_column_bound_in_markup()
    {
        const string sample = """
            <RadzenTextBox @bind-Value="attr.StringValue" />
            <RadzenNumeric Value="@value.Int32Value" />
            @* attr.DoubleValue in a comment is no binding *@
            """;

        Assert.Equal(["StringValue", "Int32Value"], ValueColumn().Matches(WithoutComments(sample)).Select(match => match.Groups["column"].Value));
    }

    private static IEnumerable<(string Path, string Text)> Markup() =>
        RepoFiles.CommittedTextFiles(".razor", ".cs")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => source.Path.StartsWith("Staffora.Blazor/", StringComparison.Ordinal))
            .Select(source => (source.Path, File.ReadAllText(source.File)));

    private static string WithoutComments(string source) => Comment().Replace(source, string.Empty);

    [GeneratedRegex(@"\.(?<column>StringValue|Int32Value|DoubleValue|BooleanValue|DateValue)\b")]
    private static partial Regex ValueColumn();

    [GeneratedRegex(@"@\*.*?\*@|<!--.*?-->|(?<!:)//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
