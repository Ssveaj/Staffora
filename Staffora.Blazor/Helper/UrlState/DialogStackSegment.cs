using System.Text;

namespace Staffora.Blazor.Helper.UrlState;

public readonly record struct DialogStackSegment
{
    public const char ChainSeparator = '.';

    public const int MaxDepth = 8;

    public DialogStackSegment(string token, string id)
    {
        if (!IsValidToken(token))
        {
            throw new ArgumentException($"\"{token}\" is not a dialog token: use 1 to 32 lowercase ASCII letters or digits, starting with a letter.", nameof(token));
        }

        if (!IsValidId(id))
        {
            throw new ArgumentException($"\"{id}\" is not a dialog id: use 1 to 64 ASCII letters, digits or hyphens.", nameof(id));
        }

        Token = token;
        Id = id;
    }

    public DialogStackSegment(string token, Guid id)
        : this(token, id == Guid.Empty ? string.Empty : id.ToString("D"))
    {
    }

    public string Token { get; }

    public string Id { get; }

    public Guid? GuidId => Guid.TryParseExact(Id, "D", out var value) && value != Guid.Empty ? value : null;

    public override string ToString() => $"{Token}-{Id}";

    public static bool IsValidToken(string? token) =>
        token is { Length: > 0 and <= 32 }
        && token[0] is >= 'a' and <= 'z'
        && token.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    public static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= 64 }
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    public static bool TryParse(string? text, out DialogStackSegment segment)
    {
        segment = default;
        var split = text?.IndexOf('-') ?? -1;
        if (text is null || split <= 0 || split == text.Length - 1)
        {
            return false;
        }

        var written = text[..split];
        var token = written.All(char.IsAsciiLetterOrDigit) ? written.ToLowerInvariant() : written;
        var id = text[(split + 1)..];
        if (!IsValidToken(token) || !IsValidId(id))
        {
            return false;
        }

        segment = new DialogStackSegment(token, Guid.TryParseExact(id, "D", out var key) && key != Guid.Empty ? key.ToString("D") : id);
        return true;
    }

    public static IReadOnlyList<DialogStackSegment> ParseChain(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > UrlStateKeys.MaxValueLength)
        {
            return [];
        }

        var chain = new List<DialogStackSegment>();
        foreach (var part in value.Split(ChainSeparator))
        {
            if (chain.Count == MaxDepth || !TryParse(part, out var segment))
            {
                break;
            }

            chain.Add(segment);
        }

        return chain;
    }

    public static string? FormatChain(IEnumerable<DialogStackSegment> chain)
    {
        var builder = new StringBuilder();
        foreach (var segment in chain.Take(MaxDepth))
        {
            if (builder.Length > 0)
            {
                builder.Append(ChainSeparator);
            }

            builder.Append(segment.ToString());
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }
}
