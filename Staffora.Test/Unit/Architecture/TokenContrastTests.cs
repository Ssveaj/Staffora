using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Staffora.Blazor.Helper;
using Staffora.Core;
using Staffora.Core.Annotations;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class TokenContrastTests
{
    public static TheoryData<string, string> TextPairs { get; } = new()
    {
        { "--color-text", "--color-canvas" },
        { "--color-text", "--color-surface" },
        { "--color-text", "--color-surface-muted" },
        { "--color-text", "--color-surface-sunken" },
        { "--color-text", "--color-surface-hover" },
        { "--color-text", "--color-accent-soft" },
        { "--color-text", "--color-accent-tint" },
        { "--color-text", "--color-success-soft" },
        { "--color-text", "--color-warning-soft" },
        { "--color-text", "--color-danger-soft" },
        { "--color-text", "--color-info-soft" },
        { "--color-text", "--color-chrome" },

        { "--color-text-muted", "--color-canvas" },
        { "--color-text-muted", "--color-surface" },
        { "--color-text-muted", "--color-surface-muted" },
        { "--color-text-muted", "--color-surface-sunken" },
        { "--color-text-muted", "--color-surface-hover" },
        { "--color-text-muted", "--color-chrome" },

        { "--color-text-subtle", "--color-canvas" },
        { "--color-text-subtle", "--color-surface" },
        { "--color-text-subtle", "--color-surface-muted" },
        { "--color-text-subtle", "--color-surface-hover" },
        { "--color-text-subtle", "--color-chrome" },

        { "--color-accent", "--color-canvas" },
        { "--color-accent", "--color-surface" },
        { "--color-accent", "--color-surface-muted" },
        { "--color-accent", "--color-surface-hover" },
        { "--color-accent", "--color-accent-soft" },
        { "--color-accent", "--color-accent-tint" },
        { "--color-accent-strong", "--color-accent-soft" },
        { "--color-accent-strong", "--color-chrome" },

        { "--color-on-accent", "--color-accent" },
        { "--color-on-accent", "--color-accent-strong" },

        { "--color-success", "--color-canvas" },
        { "--color-success", "--color-success-soft" },
        { "--color-on-accent", "--color-success" },
        { "--color-warning", "--color-canvas" },
        { "--color-warning", "--color-warning-soft" },
        { "--color-on-accent", "--color-warning" },

        { "--color-warning-strong", "--color-warning-soft" },
        { "--color-warning-strong", "--color-warning-border" },
        { "--color-warning-strong", "--color-accent-tint" },
        { "--color-warning-strong", "--color-accent-soft" },
        { "--color-warning-strong", "--color-surface-sunken" },
        { "--color-warning-strong", "--color-surface-muted" },
        { "--color-danger", "--color-canvas" },
        { "--color-danger", "--color-danger-soft" },
        { "--color-on-accent", "--color-danger" },
        { "--color-info", "--color-canvas" },
        { "--color-info", "--color-info-soft" },
        { "--color-on-accent", "--color-info" },
        { "--color-neutral", "--color-canvas" },
        { "--color-neutral", "--color-neutral-soft" },
        { "--color-on-accent", "--color-neutral" },

        { "--color-on-accent", "--sch-band-staff" },
        { "--sch-absence-text", "--sch-absence" },
        { "--sch-absence-text", "--sch-absence-light" },
        { "--color-text", "--sch-day-work" },

        { "--color-holiday", "--color-holiday-soft" },
        { "--color-holiday", "--color-surface" },
        { "--color-holiday", "--color-surface-sunken" },
        { "--color-holiday", "--color-canvas" },

        { "--color-on-accent", "--color-danger-dot" },

        { "--color-on-ink", "--color-ink" },
        { "--color-on-ink", "--color-ink-strong" },
        { "--color-on-ink-muted", "--color-ink" },
        { "--color-on-ink-muted", "--color-ink-strong" },
        { "--color-ink-accent", "--color-ink" },
        { "--color-ink-accent", "--color-ink-strong" },
        { "--color-ink-warning", "--color-ink" },
        { "--color-ink-warning", "--color-ink-strong" },
        { "--color-ink-danger", "--color-ink" },
        { "--color-ink-danger", "--color-ink-strong" },
    };

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void Every_text_pair_of_the_token_table_reaches_4_5_to_1(string foreground, string background)
    {
        var tokens = LightTokens();

        var ratio = ColorContrast.Ratio(Resolve(foreground, tokens), Resolve(background, tokens));

        Assert.True(ratio >= ColorContrast.TextMinimum, $"{foreground} on {background}: {ratio:0.00}:1");
    }

    [Theory]
    [InlineData("--color-holiday", "--color-holiday-soft")]
    [InlineData("--color-holiday", "--color-surface")]
    [InlineData("--color-holiday", "--color-surface-sunken")]
    [InlineData("--color-holiday", "--color-canvas")]
    public void The_holiday_colour_reaches_4_5_to_1_in_the_dark_scheme_too(string foreground, string background)
    {
        var tokens = DarkTokens();

        var ratio = ColorContrast.Ratio(Resolve(foreground, tokens), Resolve(background, tokens));

        Assert.True(ratio >= ColorContrast.TextMinimum, $"{foreground} on {background} in the dark scheme: {ratio:0.00}:1");
    }

    [Theory]
    [InlineData("--color-text-faint")]
    [InlineData("--color-text-placeholder")]
    public void The_decorative_greys_fall_short_on_white_and_so_never_carry_information(string token)
    {
        var tokens = LightTokens();

        Assert.True(ColorContrast.Ratio(Resolve(token, tokens), Resolve("--color-canvas", tokens)) < ColorContrast.TextMinimum);
    }

    [Fact]
    public void The_text_colour_the_contrast_choice_uses_is_the_text_token()
    {
        Assert.Equal(ColorContrast.Text, Resolve("--color-text", LightTokens()));
    }

    [Fact]
    public void Every_state_colour_gets_a_fill_and_a_text_colour_that_reach_4_5_to_1()
    {
        var colours = StateColours();
        Assert.NotEmpty(colours);

        Assert.All(colours, entry =>
        {
            Assert.True(ColorContrast.IsHex(entry.Colour), $"{entry.State} is styled {entry.Colour}, which is not a hex colour the contrast choice can judge");
            var choice = ColorContrast.OnFill(entry.Colour);

            Assert.True(ColorContrast.Ratio(choice.Foreground, choice.Fill) >= ColorContrast.TextMinimum, $"{entry.State} ({entry.Colour}): {choice.Foreground} on {choice.Fill}");
            Assert.Contains(choice.Foreground, new[] { ColorContrast.White, ColorContrast.Text });
        });
    }

    [Theory]
    [InlineData("#795548", "#795548", ColorContrast.White)]
    [InlineData("#FF9800", "#ff9800", ColorContrast.Text)]
    [InlineData("#4CAF50", "#4caf50", ColorContrast.Text)]
    [InlineData("#757575", "#757575", ColorContrast.White)]
    public void The_choice_keeps_the_fill_and_takes_white_first_then_the_text_colour(string state, string fill, string foreground)
    {
        Assert.Equal(new ColorContrast.Choice(fill, foreground), ColorContrast.OnFill(state));
    }

    [Fact]
    public void Every_state_colour_frames_its_dialog_with_text_that_reaches_4_5_to_1()
    {
        Assert.All(StateColours(), entry =>
        {
            var colors = DialogStateColors.For(entry.Colour);
            Assert.NotNull(colors);
            Assert.True(ColorContrast.Ratio(colors.HeaderText, colors.HeaderFill) >= ColorContrast.TextMinimum, $"{entry.State} header: {colors.HeaderText} on {colors.HeaderFill}");
            Assert.True(ColorContrast.Ratio(colors.PillText, colors.PillFill) >= ColorContrast.TextMinimum, $"{entry.State} pill: {colors.PillText} on {colors.PillFill}");
            Assert.True(ColorContrast.Ratio(colors.StateText, ColorContrast.White) >= ColorContrast.TextMinimum, $"{entry.State} tab: {colors.StateText} on white");
        });

        Assert.Null(DialogStateColors.For("teal"));
        Assert.Equal(ColorContrast.White, DialogStateColors.For("#FF9800")!.HeaderText);
        Assert.NotEqual("#ff9800", DialogStateColors.For("#FF9800")!.HeaderFill);
        Assert.Equal(ColorContrast.White, DialogStateColors.For("#37DE57")!.HeaderText);
        Assert.Equal(ColorContrast.White, DialogStateColors.For("#795548")!.HeaderText);
        Assert.Equal("#795548", DialogStateColors.For("#795548")!.HeaderFill);
        Assert.Equal("#0f766e", ColorContrast.AsText("#0f766e", ColorContrast.White));
    }

    [Fact]
    public void A_fill_on_which_neither_reaches_4_5_to_1_is_darkened_until_white_does()
    {
        var choice = ColorContrast.OnFill("#607D8B");

        Assert.Equal(ColorContrast.White, choice.Foreground);
        Assert.NotEqual("#607d8b", choice.Fill);
        Assert.InRange(ColorContrast.Ratio(choice.Fill, ColorContrast.White), ColorContrast.TextMinimum, 6);
    }

    [Theory]
    [InlineData("#ffffff", "#000000", 21.0)]
    [InlineData("#0f766e", "#ffffff", 5.47)]
    [InlineData("#64748b", "#fff", 4.76)]
    public void The_ratio_is_the_WCAG_contrast_ratio(string first, string second, double expected)
    {
        Assert.Equal(expected, ColorContrast.Ratio(first, second), precision: 2);
    }

    private static List<(string State, string Colour)> StateColours()
    {
        var accent = Resolve("--color-accent", LightTokens());
        var colours = typeof(CoreExtensions).Assembly.GetTypes()
            .Where(type => type.IsEnum)
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Select(field => (State: $"{field.DeclaringType!.Name}.{field.Name}", Styling: field.GetCustomAttribute<StateStylingAttribute>()))
            .Where(entry => entry.Styling is not null)
            .Select(entry => (entry.State, Colour: entry.Styling!.Color))
            .ToList();

        Assert.Equal("var(--rz-primary)", CoreExtensions.FallbackStateColor);
        colours.Add(("the fallback state colour (the accent)", accent));
        return colours;
    }

    private static Dictionary<string, string> LightTokens()
    {
        var css = File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor", "wwwroot", "app.tokens.css"));
        var withoutComments = CssComment().Replace(css, string.Empty);
        var start = withoutComments.IndexOf(":root", StringComparison.Ordinal);
        var open = withoutComments.IndexOf('{', start);
        var close = withoutComments.IndexOf('}', open);

        return Declaration().Matches(withoutComments[(open + 1)..close])
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value.Trim(), StringComparer.Ordinal);
    }

    private static Dictionary<string, string> DarkTokens()
    {
        var css = CssComment().Replace(File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor", "wwwroot", "app.tokens.css")), string.Empty);
        var media = css.IndexOf("@media (prefers-color-scheme: dark)", StringComparison.Ordinal);
        var open = css.IndexOf('{', css.IndexOf(":root", media, StringComparison.Ordinal));
        var close = css.IndexOf('}', open);
        var tokens = LightTokens();
        foreach (Match match in Declaration().Matches(css[(open + 1)..close]))
        {
            tokens[match.Groups["name"].Value] = match.Groups["value"].Value.Trim();
        }

        return tokens;
    }

    private static string Resolve(string token, IReadOnlyDictionary<string, string> tokens)
    {
        Assert.True(tokens.TryGetValue(token, out var value), $"{token} is not declared in app.tokens.css");
        return ResolveValue(value, tokens);
    }

    private static string ResolveValue(string value, IReadOnlyDictionary<string, string> tokens)
    {
        if (ColorContrast.IsHex(value))
        {
            return value.ToLowerInvariant();
        }

        if (Reference().Match(value) is { Success: true } reference)
        {
            return Resolve(reference.Groups["name"].Value, tokens);
        }

        if (MixWith().Match(value) is { Success: true } mix)
        {
            var colour = ResolveValue(mix.Groups["colour"].Value.Trim(), tokens);
            var share = double.Parse(mix.Groups["share"].Value, CultureInfo.InvariantCulture) / 100;
            var other = mix.Groups["other"].Value == "white" ? 255 : 0;
            return "#" + string.Concat(Enumerable.Range(0, 3).Select(channel =>
            {
                var own = int.Parse(colour.AsSpan(1 + channel * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return ((int)Math.Round(own * share + other * (1 - share))).ToString("x2", CultureInfo.InvariantCulture);
            }));
        }

        throw new InvalidOperationException($"The token value '{value}' is not a colour this test can resolve.");
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    [GeneratedRegex(@"(?<name>--[\w-]+)\s*:\s*(?<value>[^;]+);")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"^var\((?<name>--[\w-]+)\)$")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^color-mix\(in srgb,\s*(?<colour>[^,]+?)\s+(?<share>\d+(?:\.\d+)?)%,\s*(?<other>white|black)\)$")]
    private static partial Regex MixWith();
}
