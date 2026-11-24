using System.Text.RegularExpressions;

namespace Staffora.Core.Messaging;

public sealed partial class MessageType : IEquatable<MessageType>
{
    public const int MaxKeyLength = 100;

    public MessageType(string key, MessageCategory category, string name, params IReadOnlyList<string> placeholders)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException($"\"{key}\" is not a message type key: use lowercase ASCII letters, digits and hyphens, starting with a letter, at most {MaxKeyLength} characters.", nameof(key));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (placeholders.FirstOrDefault(placeholder => !IsValidPlaceholder(placeholder)) is { } invalid)
        {
            throw new ArgumentException($"\"{invalid}\" is not a placeholder name: use a PascalCase word of ASCII letters and digits.", nameof(placeholders));
        }

        if (placeholders.Distinct(StringComparer.Ordinal).Count() != placeholders.Count)
        {
            throw new ArgumentException($"The message type \"{key}\" names a placeholder twice.", nameof(placeholders));
        }

        Key = key;
        Category = category;
        Name = name;
        Placeholders = [.. placeholders];
    }

    public string Key { get; }

    public MessageCategory Category { get; }

    public string Name { get; }

    public IReadOnlyList<string> Placeholders { get; }

    public static bool IsValidKey(string? key) => key is { Length: > 0 and <= MaxKeyLength } && KeyPattern().IsMatch(key);

    public static bool IsValidPlaceholder(string? name) => name is { Length: > 0 and <= 50 } && PlaceholderPattern().IsMatch(name);

    public bool Equals(MessageType? other) => other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as MessageType);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    public override string ToString() => Key;

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex PlaceholderPattern();
}
