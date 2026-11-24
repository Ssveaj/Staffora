using System.Text;
using System.Text.RegularExpressions;

namespace Staffora.Services.Diagnostics;

public static partial class SecretScrubber
{
    public const string Redacted = "[redacted]";

    public const int ScanHeadroom = 2_048;

    private static readonly string[] secretWithin =
    [
        "password", "passwd", "pwd", "secret", "token", "credential", "connectionstring", "signature", "securitystamp",
        "apikey", "accesskey", "accountkey", "privatekey", "signingkey", "encryptionkey", "masterkey", "subscriptionkey", "storagekey",
    ];

    private static readonly HashSet<string> secretAlone = new(StringComparer.Ordinal)
    {
        "key", "sig", "code", "hash", "otp", "authcode", "authorizationcode", "verificationcode", "resetcode",
    };

    public static string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var scrubbed = AuthorizationHeader().Replace(text, match => $"{match.Groups["name"].Value}{match.Groups["separator"].Value}{(match.Groups["scheme"].Success ? match.Groups["scheme"].Value + " " : string.Empty)}{Redacted}");
        scrubbed = CookieHeader().Replace(scrubbed, match => $"{match.Groups["name"].Value}{match.Groups["separator"].Value}{Redacted}");
        scrubbed = ScrubNamedValues(scrubbed);
        scrubbed = BearerToken().Replace(scrubbed, $"Bearer {Redacted}");
        return JsonWebToken().Replace(scrubbed, Redacted);
    }

    public static string? Scrub(string? text, int maxLength) => Cap(Scrub(Cap(text, maxLength + ScanHeadroom)), maxLength);

    public static bool IsSecretName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var compact = Compact(name);
        if (compact.EndsWith("exception", StringComparison.Ordinal))
        {
            return false;
        }

        return secretAlone.Contains(compact) || secretWithin.Any(part => compact.Contains(part, StringComparison.Ordinal));
    }

    public static string? Cap(string? text, int maxLength) => text is null || text.Length <= maxLength ? text : text[..maxLength];

    private static string ScrubNamedValues(string text)
    {
        StringBuilder? scrubbed = null;
        var copied = 0;
        var at = 0;
        while (at < text.Length)
        {
            var pair = NamedValue().Match(text, at);
            if (!pair.Success)
            {
                break;
            }

            var valueStart = pair.Index + pair.Length;
            at = valueStart;
            if (!IsSecretName(pair.Groups["name"].Value))
            {
                continue;
            }

            var value = Value().Match(text, valueStart);
            if (!value.Success)
            {
                continue;
            }

            scrubbed ??= new StringBuilder(text.Length);
            scrubbed.Append(text, copied, valueStart - copied).Append(Redacted);
            copied = at = valueStart + value.Length;
        }

        return scrubbed is null ? text : scrubbed.Append(text, copied, text.Length - copied).ToString();
    }

    private static string Compact(string name)
    {
        Span<char> buffer = stackalloc char[Math.Min(name.Length, 256)];
        var length = 0;
        foreach (var character in name.AsSpan(0, Math.Min(name.Length, 256)))
        {
            if (character is not ('_' or '-' or '.'))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer[..length]);
    }

    [GeneratedRegex("""(?<![\w.\-])(?<name>[A-Za-z_][\w.\-]{0,100})["']?\s*[=:]\s*(?!line\s\d)""")]
    private static partial Regex NamedValue();

    [GeneratedRegex("""\G(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^\s;,&"']+)""")]
    private static partial Regex Value();

    [GeneratedRegex("""(?<name>\b(?:Proxy-)?Authorization)(?<separator>["']?\s*[=:]\s*)(?:(?<scheme>[A-Za-z][\w\-]*)\s+(?=\S))?(?:"(?:\\.|[^"\\])*"|[^\s;,&"']+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(?<name>\b(?:Set-)?Cookie)(?<separator>\s*:\s*)[^\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex CookieHeader();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*")]
    private static partial Regex JsonWebToken();
}
