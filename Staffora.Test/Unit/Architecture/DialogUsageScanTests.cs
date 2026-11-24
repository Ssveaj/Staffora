using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class DialogUsageScanTests
{
    private const string Extensions = "Staffora.Blazor/Helper/DialogServiceExtensions.cs";

    private static readonly HashSet<string> stafforaComponents = [.. typeof(Staffora.Blazor.Components.Common.Dialogs.StateEntity.StateEntityDialogHost).Assembly.GetTypes()
        .Where(type => typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(type))
        .Select(type => type.Name)];

    private static readonly Dictionary<(string Path, string Dialog), string> allowList = new()
    {
        [(Extensions, "FormDialogHost")] = "the extensions open every dialog without a lifecycle through the common host",
        [(Extensions, "StateEntityDialogHost")] = "the extensions open every state-entity dialog through its host (OpenStateEntityDialogAsync)",
    };

    [Fact]
    public void No_dialog_opens_past_the_extensions_except_the_listed_entity_dialogs()
    {
        var offenders = Sources()
            .SelectMany(source => RawOpens(source.Text).Select(open => (source.Path, open.Call, open.Dialog)))
            .Where(open => open.Dialog is null || !allowList.ContainsKey((open.Path, open.Dialog)))
            .Select(open => $"{open.Path}: {open.Call}<{open.Dialog}>");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_allow_list_entry_still_opens_its_dialog_and_names_a_reason()
    {
        var opened = Sources()
            .SelectMany(source => RawOpens(source.Text).Where(open => open.Dialog is not null).Select(open => (source.Path, open.Dialog!)))
            .ToHashSet();

        var stale = allowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !opened.Contains(entry.Key))
            .Select(entry => $"{entry.Key.Path}: {entry.Key.Dialog}");

        Assert.Empty(stale);
    }

    [Fact]
    public void No_state_entity_dialog_body_is_opened_as_a_form_dialog()
    {
        var bodies = typeof(Staffora.Blazor.Components.Common.Dialogs.StateEntity.StateEntityDialogHost).Assembly.GetTypes()
            .Where(type => type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(Staffora.Blazor.Components.Common.Dialogs.StateEntity.IStateEntityDialog<>)))
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = Sources()
            .SelectMany(source => FormOpener().Matches(WithoutComments(source.Text)).Select(match => (source.Path, Dialog: match.Groups["dialog"].Value)))
            .Where(open => bodies.Contains(open.Dialog))
            .Select(open => $"{open.Path}: OpenFormDialogAsync<{open.Dialog}>");

        Assert.Empty(offenders);
        Assert.Equal(["TestParentDialog"], FormOpener().Matches("await dialogService.OpenFormDialogAsync<TestParentDialog, bool>(title);").Select(match => match.Groups["dialog"].Value));
    }
    [Fact]
    public void No_file_uses_radzens_notifications()
    {
        var offenders = Sources()
            .Where(source => RadzenNotifications().IsMatch(WithoutComments(source.Text)))
            .Select(source => source.Path);

        Assert.Empty(offenders);
        Assert.Matches(RadzenNotifications(), "@inject NotificationService NotificationService");
        Assert.Matches(RadzenNotifications(), "<RadzenNotification />");
        Assert.Matches(RadzenNotifications(), "Notify(new NotificationMessage { Severity = NotificationSeverity.Error });");
        Assert.DoesNotMatch(RadzenNotifications(), "@inject NoticeService Notices <NotificationBell />");
        Assert.DoesNotMatch(RadzenNotifications(), WithoutComments("@* <RadzenNotification /> was the old host *@"));
    }

    [Fact]
    public void The_scan_catches_every_raw_opener_and_ignores_the_extensions_and_comments()
    {
        const string sample = """"
            var result = await DialogService.OpenAsync<NoteDialog>("New", parameters, options);
            await dialogService.OpenAsync("Untyped", builder => { }, new DialogOptions());
            var confirmed = await DialogService.Confirm("Delete?", "Confirm");
            await DialogService.Alert(result.Error, "Error");
            await DialogService.OpenSideAsync<Panel>("Side");
            DialogService.Open<Legacy>("Old");
            var ok = await DialogService.ConfirmAsync("Delete?");
            await DialogService.OpenFormDialogAsync<AddUserToRoleDialog, Guid?>("Add", p => p.Set(d => d.Users, users));
            await DialogService.ShowErrorAsync(result.Error);
            // DialogService.OpenAsync<InAComment>() is no call.
            @* DialogService.Confirm("in a Razor comment") *@
            /* DialogService.Alert("in a block comment") */
            await DialogService.OpenAsync<Grid<Guid>>("Nested", parameters);
            await DialogService.OpenAsync<Picker<Dictionary<string, List<Guid>>>>("Deeper");
            Func<string, string, ConfirmOptions?, Task<bool?>> ask = DialogService.Confirm;
            var opener = _dialogService.OpenAsync<MethodGroup>;
            var url = "https://example.test//path"; var note = "a // b"; await DialogService.Alert("after a line comment in a string");
            var glob = "Pages/*.razor"; await DialogService.Open<AfterABlockCommentInAString>("x"); var end = "*/";
            var at = @"C:\temp\"; var raw = """// "raw" """; await DialogService.OpenSideAsync<AfterStrings>("y");
            var quote = '"'; await DialogService.Confirm("after a quote character");
            await UserDialog.OpenAsync(DialogService, users, id); await DialogService.OpenAsync<UserDialog>("Raw");
            await UserDialog.OpenAsync<NoteDialog>("A component's name on Radzen's generic opener"); await UserDialog.OpenAsync("A component's name before a title");
            """";

        Assert.Equal(
            [
                ("OpenAsync", "NoteDialog"), ("OpenAsync", null), ("Confirm", null), ("Alert", null), ("OpenSideAsync", "Panel"), ("Open", "Legacy"),
                ("OpenAsync", "Grid"), ("OpenAsync", "Picker"), ("Confirm", null), ("OpenAsync", "MethodGroup"),
                ("Alert", null), ("Open", "AfterABlockCommentInAString"), ("OpenSideAsync", "AfterStrings"), ("Confirm", null),
                ("OpenAsync", "UserDialog"), ("OpenAsync", "NoteDialog"), ("OpenAsync", null),
            ],
            RawOpens(sample));
    }

    [Fact]
    public void Strings_are_kept_while_comments_go_so_a_call_inside_a_razor_attribute_still_counts()
    {
        const string sample = """
            <RadzenButton Click="@(() => DialogService.Confirm("Leave?"))" />
            var text = "the // in this string ends no line"; var other = "nor does /* this one";
            // DialogService.Alert("commented out");
            """;

        Assert.Equal([("Confirm", (string?)null)], RawOpens(sample));
    }

    private static List<(string Call, string? Dialog)> RawOpens(string source) =>
        [.. RawOpener().Matches(WithoutComments(source))
            .Where(match => match.Groups["paren"].Success || match.Groups["service"].Success)
            .Where(match => !IsStafforaComponentsOwnOpener(match))
            .Select(match => (match.Groups["call"].Value, match.Groups["dialog"].Success ? match.Groups["dialog"].Value : null))];

    private static bool IsStafforaComponentsOwnOpener(Match match) =>
        match.Groups["service"].Success
        && stafforaComponents.Contains(match.Groups["service"].Value)
        && match.Groups["paren"].Success
        && !match.Groups["dialog"].Success
        && !match.Groups["title"].Success;

    private static IEnumerable<(string Path, string Text)> Sources() =>
        RepoFiles.CommittedTextFiles(".cs", ".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => source.Path.StartsWith("Staffora.Blazor/", StringComparison.Ordinal))
            .Select(source => (source.Path, File.ReadAllText(source.File)));

    private static string WithoutComments(string source) =>
        StringOrComment().Replace(source, match => match.Groups["comment"].Success ? string.Empty : match.Value);

    [GeneratedRegex(@"(?:(?<service>\b_?\w*[Dd]ialog(?:Service|s)?)\s*)?\.\s*(?<call>OpenAsync|OpenSideAsync|OpenSide|Open|Confirm|Alert)\b\s*(?:<\s*(?<dialog>[\w.]+)\s*(?:<(?:[^<>]|<(?:[^<>]|<[^<>]*>)*>)*>)?\s*>)?\s*(?<paren>\((?<title>\s*\$?@?"")?)?")]
    private static partial Regex RawOpener();

    [GeneratedRegex(@"\.\s*OpenFormDialogAsync\s*<\s*(?<dialog>\w+)")]
    private static partial Regex FormOpener();

    [GeneratedRegex(@"\bNotificationService\b|<RadzenNotification\b|\bNotificationMessage\b|\bNotificationSeverity\b")]
    private static partial Regex RadzenNotifications();

    [GeneratedRegex(""""(?<string>\$*"""[\s\S]*?"""|(?:\$@|@\$|@)"(?:[^"]|"")*"|\$?"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])')|(?<comment>(?<!:)//[^\n]*|/\*.*?\*/|@\*.*?\*@)"""", RegexOptions.Singleline)]
    private static partial Regex StringOrComment();
}
