using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class TimeSourceScanTests
{
    private static readonly string[] productionProjects = ["Staffora.Core", "Staffora.Data", "Staffora.Services", "Staffora.Blazor", "Staffora.E2ESetup"];

    private static readonly Dictionary<string, (int Reads, string RemovedBy)> systemClockAllowList = new(StringComparer.Ordinal);

    [Fact]
    public void No_production_code_reads_the_system_clock_outside_the_allow_list()
    {
        var offenders = ProductionSources()
            .Where(source => !systemClockAllowList.ContainsKey(source.Path))
            .SelectMany(source => FindSystemClockReads(source.Text).Select(hit => $"{source.Path}: {hit}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_allow_listed_file_reads_the_system_clock_exactly_its_pinned_number_of_times()
    {
        var sources = ProductionSources().ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);

        Assert.Empty(CountsOffThePin(sources, systemClockAllowList));
    }

    [Fact]
    public void The_pinned_count_catches_a_new_read_and_a_removed_one_in_an_allow_listed_file()
    {
        var allowList = new Dictionary<string, (int Reads, string RemovedBy)> { ["Listed.cs"] = (1, "a later step") };

        var unchanged = CountsOffThePin(new Dictionary<string, string> { ["Listed.cs"] = "Created = DateTime.UtcNow;" }, allowList);
        var added = CountsOffThePin(new Dictionary<string, string> { ["Listed.cs"] = "Created = DateTime.UtcNow; Modified = DateTime.Now;" }, allowList);
        var removed = CountsOffThePin(new Dictionary<string, string> { ["Listed.cs"] = "Created = clock.GetUtcNow();" }, allowList);

        Assert.Empty(unchanged);
        Assert.Equal(["Listed.cs: pinned 1, found 2"], added);
        Assert.Equal(["Listed.cs: pinned 1, found 0"], removed);
    }

    [Fact]
    public void The_clock_scan_catches_every_system_clock_read()
    {
        const string sample = """
            var a = DateTime.Now;
            var b = DateTime.UtcNow;
            var c = DateTimeOffset.UtcNow;
            var d = DateTime.Today;
            var e = timeProvider.GetUtcNow();
            """;

        Assert.Equal(["DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.UtcNow", "DateTime.Today"], FindSystemClockReads(sample));
    }

    [Fact]
    public void No_production_member_named_for_a_date_is_a_DateTime()
    {
        var offenders = ProductionSources()
            .SelectMany(source => FindDateTimeMembersNamedForADay(source.Text).Select(hit => $"{source.Path}: {hit}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_date_member_scan_catches_a_DateTime_named_for_a_date()
    {
        const string sample = """
            public DateTime StartDate { get; set; }
            public DateTime? EndDay { get; init; }
            private DateTimeOffset BirthDate;
            public sealed record Period(DateTime FirstDate, DateOnly LastDate);
            public DateOnly PeriodStartDate { get; set; }
            public DateTime Created { get; set; }
            public DateTime Today => default;
            void Book(DateTime startDate) { }
            public DateTime Birthday { get; set; }
            public DateTime[] HolidayDates { get; init; } = [];
            public IReadOnlyList<DateTimeOffset> OpenDays { get; init; } = [];
            public List<DateTime>? BlockedDates { get; set; }
            public DateOnly[] PeriodDates { get; set; } = [];
            public TimeSpan Days { get; set; }
            """;

        Assert.Equal(
            ["StartDate", "EndDay", "BirthDate", "FirstDate", "Birthday", "HolidayDates", "OpenDays", "BlockedDates"],
            FindDateTimeMembersNamedForADay(sample));
    }

    private static List<string> FindSystemClockReads(string source) =>
        SystemClockRead().Matches(source).Select(match => match.Value).ToList();

    private static List<string> CountsOffThePin(IReadOnlyDictionary<string, string> sources, IReadOnlyDictionary<string, (int Reads, string RemovedBy)> allowList) =>
        allowList
            .Select(entry => (Path: entry.Key, Pinned: entry.Value.Reads, Found: sources.TryGetValue(entry.Key, out var text) ? FindSystemClockReads(text).Count : 0))
            .Where(entry => entry.Found != entry.Pinned)
            .Select(entry => $"{entry.Path}: pinned {entry.Pinned}, found {entry.Found}")
            .ToList();

    private static List<string> FindDateTimeMembersNamedForADay(string source) =>
        DateTimeMemberNamedForADay().Matches(source).Select(match => match.Groups["name"].Value).ToList();

    private static IEnumerable<(string Path, string Text)> ProductionSources() =>
        RepoFiles.CommittedTextFiles(".cs", ".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => productionProjects.Any(project => source.Path.StartsWith(project + "/", StringComparison.Ordinal)))
            .Select(source => (source.Path, File.ReadAllText(source.File)));

    [GeneratedRegex(@"\bDateTime(?:Offset)?\.(?:UtcNow|Now|Today)\b")]
    private static partial Regex SystemClockRead();

    [GeneratedRegex(@"\bDateTime(?:Offset)?\??(?:\[\]|>)*\??\s+(?<name>(?:[A-Z]\w*)?(?:Date|Day|Birthday)s?)\s*[{;=(,)]")]
    private static partial Regex DateTimeMemberNamedForADay();
}
