using System.Globalization;
using System.Text;

namespace Staffora.Core.Entities.EntityAttributes;

public sealed record AttributeChoiceOption(string Key, string Label, bool IsRetired = false)
{
    public const int MaxOptions = 50;

    public const int LabelMaxLength = 100;

    public const int KeyMaxLength = 40;

    private const int SlugMaxLength = 32;

    private static readonly Dictionary<char, string> spelledAs = new()
    {
        ['ß'] = "ss",
        ['æ'] = "ae",
        ['œ'] = "oe",
        ['ø'] = "o",
        ['ł'] = "l",
        ['đ'] = "d",
        ['ð'] = "d",
        ['þ'] = "th",
        ['ı'] = "i",
        ['ħ'] = "h",
    };

    public static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key)
        && key.Length <= KeyMaxLength
        && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && key[0] != '-' && key[^1] != '-'
        && !key.Contains("--", StringComparison.Ordinal);

    public static string NewKey(string? label, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var spelled = new StringBuilder();
        foreach (var c in (label ?? string.Empty).ToLowerInvariant())
        {
            if (spelledAs.TryGetValue(c, out var letters))
            {
                spelled.Append(letters);
            }
            else
            {
                spelled.Append(c);
            }
        }

        var slug = new StringBuilder();
        foreach (var c in spelled.ToString().Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                slug.Append(c);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                && slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }

            if (slug.Length >= SlugMaxLength)
            {
                break;
            }
        }

        var stem = slug.ToString().Trim('-');
        if (stem.Length == 0)
        {
            stem = "option";
        }

        var key = stem;
        for (var suffix = 2; taken.Contains(key); suffix++)
        {
            key = $"{stem}-{suffix}";
        }

        return key;
    }

    public static List<AttributeChoiceOption> WithKeys(IEnumerable<AttributeChoiceOption> options, IEnumerable<string> reserved)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reserved);
        var stored = new HashSet<string>(reserved, StringComparer.Ordinal);
        var taken = new HashSet<string>(stored, StringComparer.Ordinal);
        var keyed = new List<AttributeChoiceOption>();
        foreach (var option in options)
        {
            var label = option.Label?.Trim() ?? string.Empty;
            if (option.Key is { Length: > 0 } key && stored.Contains(key))
            {
                keyed.Add(option with { Label = label });
            }
            else
            {
                var made = NewKey(label, taken);
                taken.Add(made);
                keyed.Add(option with { Key = made, Label = label });
            }
        }

        return keyed;
    }
}
