namespace Staffora.Core.Entities;

public sealed class NameAndDescriptionRule
{
    public NameAndDescriptionRule(string noun, int nameMaxLength, int descriptionMaxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noun);
        Noun = noun;
        NameMaxLength = nameMaxLength;
        DescriptionMaxLength = descriptionMaxLength;
    }

    public string Noun { get; }

    public int NameMaxLength { get; }

    public int DescriptionMaxLength { get; }

    public string NameMissing => $"A {Noun} needs a name.";

    public string NameTooLong => $"A {Noun}'s name has at most {NameMaxLength} characters.";

    public string DescriptionTooLong => $"A {Noun}'s description has at most {DescriptionMaxLength} characters.";

    public string? Problem(string name, string? description)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return NameMissing;
        }

        if (name.Length > NameMaxLength)
        {
            return NameTooLong;
        }

        return description?.Length > DescriptionMaxLength ? DescriptionTooLong : null;
    }
}
