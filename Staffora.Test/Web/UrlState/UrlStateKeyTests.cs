using Staffora.Blazor.Helper.UrlState;

namespace Staffora.Test.Web.UrlState;

public class UrlStateKeyTests
{
    private enum StatusFilter { All, Active, Inactive }

    [Fact]
    public void Text_is_trimmed_round_trips_and_leaves_empty_out()
    {
        var key = UrlStateKey.Text("q", maxLength: 10);

        Assert.Equal("ann lee", key.Format("  ann lee "));
        Assert.Equal("ann lee", key.Parse(key.Format("  ann lee ")));
        Assert.Null(key.Format(string.Empty));
        Assert.Null(key.Format("   "));
        Assert.Equal(string.Empty, key.Parse(null));
        Assert.Equal(string.Empty, key.Parse("   "));
    }

    [Fact]
    public void Text_longer_than_its_limit_reads_as_empty_and_is_cut_on_write()
    {
        var key = UrlStateKey.Text("q", maxLength: 5);

        Assert.Equal(string.Empty, key.Parse("abcdefgh"));
        Assert.Equal("abcde", key.Format("abcdefgh"));
        Assert.Equal(string.Empty, key.Parse(new string('a', UrlStateKeys.MaxValueLength + 1)));
    }

    [Fact]
    public void An_enum_is_written_in_lowercase_read_in_any_case_and_its_default_left_out()
    {
        var key = UrlStateKey.Enum("status", StatusFilter.All);

        Assert.Equal("inactive", key.Format(StatusFilter.Inactive));
        Assert.Equal(StatusFilter.Inactive, key.Parse("inactive"));
        Assert.Equal(StatusFilter.Inactive, key.Parse("INACTIVE"));
        Assert.Null(key.Format(StatusFilter.All));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("Active,Inactive")]
    [InlineData("deleted")]
    [InlineData(" active")]
    [InlineData("")]
    public void An_enum_reads_only_a_defined_name(string text)
    {
        var key = UrlStateKey.Enum("status", StatusFilter.All);

        Assert.Equal(StatusFilter.All, key.Parse(text));
    }

    [Fact]
    public void An_enum_reads_only_the_members_the_page_offers()
    {
        var key = UrlStateKey.Enum("status", StatusFilter.All, value => value != StatusFilter.Inactive);

        Assert.Equal(StatusFilter.Active, key.Parse("active"));
        Assert.Equal(StatusFilter.All, key.Parse("inactive"));
    }

    [Fact]
    public void An_optional_enum_is_null_by_default_and_round_trips_a_member()
    {
        var key = UrlStateKey.OptionalEnum<StatusFilter>("status");

        Assert.Null(key.Format(null));
        Assert.Equal("active", key.Format(StatusFilter.Active));
        Assert.Equal(StatusFilter.Active, key.Parse("active"));
        Assert.Null(key.Parse("1"));
        Assert.Null(key.Parse("nothing"));
    }

    [Fact]
    public void An_int_round_trips_in_invariant_digits_and_refuses_anything_else()
    {
        var key = UrlStateKey.Int("days", 7, value => value is 1 or 7 or 30 or 90);

        Assert.Equal("30", key.Format(30));
        Assert.Equal(30, key.Parse("30"));
        Assert.Null(key.Format(7));
        Assert.Equal(7, key.Parse("31"));
        Assert.Equal(7, key.Parse("3.5"));
        Assert.Equal(7, key.Parse("1e3"));
        Assert.Equal(7, key.Parse(" 30"));
        Assert.Equal(7, key.Parse("99999999999"));
        Assert.Equal(7, key.Parse("thirty"));
    }

    [Fact]
    public void A_guid_round_trips_and_refuses_another_form_or_the_empty_id()
    {
        var key = UrlStateKey.Guid("id");
        var id = Guid.NewGuid();

        Assert.Equal(id.ToString("D"), key.Format(id));
        Assert.Equal(id, key.Parse(key.Format(id)));
        Assert.Null(key.Format(null));
        Assert.Null(key.Parse(id.ToString("B")));
        Assert.Null(key.Parse(id.ToString("N")));
        Assert.Null(key.Parse(Guid.Empty.ToString("D")));
        Assert.Null(key.Parse("not-an-id"));
    }

    [Fact]
    public void A_bool_round_trips_and_reads_only_true_or_false()
    {
        var key = UrlStateKey.Bool("archived");

        Assert.Equal("true", key.Format(true));
        Assert.True(key.Parse("true"));
        Assert.True(key.Parse("TRUE"));
        Assert.Null(key.Format(false));
        Assert.False(key.Parse("yes"));
        Assert.False(key.Parse("1"));
    }

    [Fact]
    public void A_date_round_trips_as_yyyy_MM_dd_and_refuses_any_other_form()
    {
        var key = UrlStateKey.Date(UrlStateKeys.Day);

        Assert.Equal("2027-03-08", key.Format(new DateOnly(2027, 3, 8)));
        Assert.Equal(new DateOnly(2027, 3, 8), key.Parse("2027-03-08"));
        Assert.Null(key.Format(null));
        Assert.Null(key.Parse("2027-3-8"));
        Assert.Null(key.Parse("08/03/2027"));
        Assert.Null(key.Parse("2027-02-30"));
        Assert.Null(key.Parse("2027-03-08T00:00"));
        Assert.Null(UrlStateKey.Date("day", date => date.DayOfWeek == DayOfWeek.Monday).Parse("2027-03-09"));
    }

    [Fact]
    public void A_month_round_trips_as_yyyy_MM_and_refuses_any_other_form()
    {
        var key = UrlStateKey.Month(UrlStateKeys.Month, Staffora.Core.Models.OwnScheduleMonth.IsReadable);

        Assert.Equal("2027-03", key.Format(new DateOnly(2027, 3, 1)));
        Assert.Equal(new DateOnly(2027, 3, 1), key.Parse("2027-03"));
        Assert.Null(key.Format(null));
        Assert.Null(key.Parse("2027-3"));
        Assert.Null(key.Parse("2027-03-10"));
        Assert.Null(key.Parse("03/2027"));
        Assert.Null(key.Parse("2027-13"));
        Assert.Null(key.Parse("0001-01"));
        Assert.Null(key.Parse("9999-12"));
    }

    [Fact]
    public void A_parser_that_throws_reads_as_the_default()
    {
        var key = new UrlStateKey<int>("n", 3, text => (true, int.Parse(text, System.Globalization.CultureInfo.InvariantCulture)), value => value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(3, key.Parse("not a number"));
        Assert.Equal(3, key.Parse("99999999999"));
    }

    [Fact]
    public void A_parser_or_check_that_throws_any_exception_reads_as_the_default()
    {
        var throwingParser = new UrlStateKey<int>("n", 3, _ => throw new InvalidOperationException("The parser broke."), value => value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var throwingCheck = UrlStateKey.Int("days", 7, _ => throw new NullReferenceException());
        var throwingEnumCheck = UrlStateKey.Enum("status", StatusFilter.All, _ => throw new KeyNotFoundException());

        Assert.Equal(3, throwingParser.Parse("5"));
        Assert.Equal(7, throwingCheck.Parse("30"));
        Assert.Equal(StatusFilter.All, throwingEnumCheck.Parse("active"));
    }

    [Fact]
    public void Text_written_as_null_leaves_the_address()
    {
        var key = UrlStateKey.Text("q");

        Assert.Null(key.Format(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("1q")]
    [InlineData("my-key")]
    [InlineData("q&x")]
    [InlineData("averyveryverylongname")]
    [InlineData("slots.")]
    [InlineData(".q")]
    [InlineData("a.b.c")]
    [InlineData("Slots.q")]
    [InlineData("slots..q")]
    public void A_key_name_must_need_no_encoding(string name)
    {
        Assert.Throws<ArgumentException>(() => UrlStateKey.Text(name));
    }

    [Fact]
    public void A_prefixed_name_of_two_plain_names_joined_by_a_dot_is_a_key_name()
    {
        Assert.Equal("slots.q", UrlStateKey.Text("slots.q").Name);
        Assert.True(UrlStateKeys.IsValidName("units2.inactive"));
    }

    [Fact]
    public void A_dialog_chain_round_trips_and_ends_at_the_first_segment_that_does_not_read()
    {
        var unit = Guid.NewGuid();
        var chain = new[] { new DialogStackSegment("tenant", unit), new DialogStackSegment("role", "42") };

        var text = DialogStackSegment.FormatChain(chain);

        Assert.Equal($"tenant-{unit:D}.role-42", text);
        Assert.Equal(chain, DialogStackSegment.ParseChain(text));
        Assert.Equal(unit, DialogStackSegment.ParseChain(text)[0].GuidId);
        Assert.Null(DialogStackSegment.FormatChain([]));
        Assert.Equal([new DialogStackSegment("tenant", "1")], DialogStackSegment.ParseChain("tenant-1.ba_d-2.role-3"));
        Assert.Empty(DialogStackSegment.ParseChain("-1"));
        Assert.Empty(DialogStackSegment.ParseChain("tenant-"));
        Assert.Empty(DialogStackSegment.ParseChain("tenant-a b"));
        Assert.Empty(DialogStackSegment.ParseChain("1tenant-1"));
        Assert.Empty(DialogStackSegment.ParseChain("Key-1"));
        Assert.Throws<ArgumentException>(() => new DialogStackSegment("tenant", Guid.Empty));
        Assert.Throws<ArgumentException>(() => new DialogStackSegment("Tenant", "1"));
    }

    [Fact]
    public void A_dialog_segment_is_read_in_any_case_and_kept_in_the_form_the_app_writes()
    {
        var row = Guid.NewGuid();
        var canonical = new DialogStackSegment("testparent", row);

        Assert.Equal([canonical], DialogStackSegment.ParseChain($"testparent-{row.ToString("D").ToUpperInvariant()}"));
        Assert.Equal([canonical], DialogStackSegment.ParseChain($"TestParent-{row:D}"));
        Assert.Equal([canonical, canonical], DialogStackSegment.ParseChain($"{canonical}.{canonical.ToString().ToUpperInvariant()}"));
        Assert.Equal($"testparent-{row:D}.testparent-{row:D}", DialogStackSegment.FormatChain(DialogStackSegment.ParseChain($"TESTPARENT-{row:D}.testparent-{row.ToString("D").ToUpperInvariant()}")));

        Assert.Equal("ABC-1", DialogStackSegment.ParseChain("ROLE-ABC-1")[0].Id);
        Assert.Equal("role", DialogStackSegment.ParseChain("ROLE-ABC-1")[0].Token);
    }

    [Fact]
    public void A_dialog_chain_names_at_most_eight_dialogs_in_at_most_512_characters()
    {
        Assert.Equal(8, DialogStackSegment.MaxDepth);
        Assert.Equal(512, UrlStateKeys.MaxValueLength);
        Assert.Equal(8, DialogStackSegment.ParseChain(string.Join('.', Enumerable.Range(1, 20).Select(i => $"d-{i}"))).Count);
        Assert.Equal(8, DialogStackSegment.FormatChain(Enumerable.Range(1, 9).Select(i => new DialogStackSegment("d", $"{i}")))!.Split('.').Length);

        var ids = Enumerable.Repeat(61, 7).Append(62).Select(length => new string('a', length)).ToList();
        var exact = string.Join('.', ids.Select(id => $"d-{id}"));
        Assert.Equal(512, exact.Length);
        Assert.Equal(8, DialogStackSegment.ParseChain(exact).Count);
        Assert.Empty(DialogStackSegment.ParseChain(exact + "a"));
    }

    [Fact]
    public void Every_dialog_token_is_a_valid_token_and_no_two_are_alike()
    {
        var tokens = typeof(DialogTokens).GetFields().Select(field => (string)field.GetRawConstantValue()!).ToList();

        Assert.NotEmpty(tokens);
        Assert.All(tokens, token => Assert.True(DialogStackSegment.IsValidToken(token), token));
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_query_key_name_is_valid_and_no_two_are_alike()
    {
        var names = typeof(UrlStateKeys).GetFields().Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!).ToList();

        Assert.Contains(UrlStateKeys.DialogStack, names);
        Assert.All(names, name => Assert.True(UrlStateKeys.IsValidName(name), name));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }
}
