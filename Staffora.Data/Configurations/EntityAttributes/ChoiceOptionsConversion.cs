using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Staffora.Core.Entities.EntityAttributes;

namespace Staffora.Data.Configurations.EntityAttributes;

public static class ChoiceOptionsConversion
{
    private static readonly JsonSerializerOptions jsonOptions = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static ValueConverter<List<AttributeChoiceOption>, string> Converter { get; } = new(options => Write(options), json => Read(json));

    public static ValueComparer<List<AttributeChoiceOption>> Comparer { get; } = new(
        (first, second) => SameOptions(first, second),
        options => HashOf(options),
        options => Copy(options));

    public static string Write(List<AttributeChoiceOption>? options) => JsonSerializer.Serialize(options ?? [], jsonOptions);

    public static List<AttributeChoiceOption> Read(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<AttributeChoiceOption>>(json, jsonOptions) ?? [];

    private static bool SameOptions(List<AttributeChoiceOption>? first, List<AttributeChoiceOption>? second) =>
        (first ?? []).SequenceEqual(second ?? []);

    private static int HashOf(List<AttributeChoiceOption>? options) =>
        (options ?? []).Aggregate(0, (hash, option) => HashCode.Combine(hash, option));

    private static List<AttributeChoiceOption> Copy(List<AttributeChoiceOption>? options) => [.. options ?? []];
}
