using System.Text.RegularExpressions;
using Staffora.Blazor.Helper;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class TimelineContrastTests
{
    [Theory]
    [InlineData("--color-on-accent", "--color-accent")]
    [InlineData("--color-info", "--color-info-soft")]
    [InlineData("--color-danger", "--color-surface")]
    [InlineData("--color-danger", "--color-danger-soft")]
    [InlineData("--color-on-accent", "--color-danger")]
    [InlineData("--color-text-inverse", "--color-text")]
    [InlineData("--color-on-accent", "--color-danger-dot")]
    [InlineData("--sch-leave-text", "--color-surface")]
    public void The_timelines_own_text_pairs_reach_four_and_a_half_to_one(string foreground, string background)
    {
        var tokens = Tokens();

        Assert.True(ColorContrast.Ratio(tokens[foreground], tokens[background]) >= ColorContrast.TextMinimum, $"{foreground} on {background}");
    }

    [Theory]
    [InlineData("--color-danger-dot", "--color-surface")]
    [InlineData("--color-danger-dot", "--color-surface-hover")]
    public void The_now_line_stands_out_at_three_to_one_from_the_track(string line, string track)
    {
        var tokens = Tokens();

        Assert.True(ColorContrast.Ratio(tokens[line], tokens[track]) >= 3, $"{line} on {track}");
    }

    private static Dictionary<string, string> Tokens()
    {
        var css = File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor", "wwwroot", "app.tokens.css"));
        var light = css[..css.IndexOf("@media", StringComparison.Ordinal)];
        return HexToken().Matches(light).ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"(?<name>--[\w-]+):\s*(?<value>#[0-9a-fA-F]{6})\s*;")]
    private static partial Regex HexToken();
}
