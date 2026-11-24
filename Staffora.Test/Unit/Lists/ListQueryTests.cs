using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Lists;

namespace Staffora.Test.Unit.Lists;

public class ListQueryTests
{
    private static readonly ListDefinition units = OrganizationUnitList.Definition;
    private static readonly ListDefinition users = UserList.Definition;

    [Fact]
    public void A_query_written_to_the_address_reads_back_as_the_same_query()
    {
        var query = units.Default
            .WithFilter(OrganizationUnitList.Type, "team")
            .WithToggle(OrganizationUnitList.ShowInactive, true) with
        {
            Search = "north",
            Group = null,
            Sort = OrganizationUnitList.SortType,
            Descending = true,
            Page = 3,
        };

        var address = units.Write(query);

        Assert.Equal("north", address[ListDefinition.SearchKey]);
        Assert.Equal("team", address[OrganizationUnitList.Type]);
        Assert.Equal("true", address[OrganizationUnitList.ShowInactive]);
        Assert.Equal(ListDefinition.NoGroup, address[ListDefinition.GroupKey]);
        Assert.Equal("-type", address[ListDefinition.SortKey]);
        Assert.Equal("3", address[ListDefinition.PageKey]);
        Assert.Equal(query, units.Read(key => address.GetValueOrDefault(key)));

        var users = ListQueryTests.users.Default.WithFilter(UserList.Status, UserList.Inactive) with { Group = UserList.ByStatus, Sort = UserList.SortEmail };
        var userAddress = ListQueryTests.users.Write(users);
        Assert.Equal(("inactive", "status", "email"), (userAddress[UserList.Status], userAddress[ListDefinition.GroupKey], userAddress[ListDefinition.SortKey]));
        Assert.Equal(users, ListQueryTests.users.Read(key => userAddress.GetValueOrDefault(key)));
    }

    [Fact]
    public void The_default_query_writes_nothing_and_an_empty_address_reads_as_the_default()
    {
        Assert.All(units.Write(units.Default).Values, Assert.Null);
        Assert.Equal(units.AddressKeys.Order(StringComparer.Ordinal), units.Write(units.Default).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(units.Default, units.Read(_ => null));
        Assert.Equal((OrganizationUnitList.ByParent, OrganizationUnitList.SortName, false, 1, ListDefinition.DefaultPageSize), (units.Default.Group, units.Default.Sort, units.Default.Descending, units.Default.Page, units.Default.PageSize));
    }

    [Theory]
    [InlineData("type", "BRANCH", "branch")]
    [InlineData("type", "branch", "branch")]
    [InlineData("type", "1", null)]
    [InlineData("type", "planet", null)]
    [InlineData("type", "", null)]
    public void A_filter_reads_only_the_values_it_offers_in_any_case(string key, string text, string? expected)
    {
        var query = units.Read(name => name == key ? text : null);

        Assert.Equal(expected, query.Filter(key));
        Assert.Equal(expected is null ? null : OrganizationUnitType.Branch, query.FilterAs<OrganizationUnitType>(key));
    }

    [Theory]
    [InlineData("-NAME", "name", true)]
    [InlineData("type", "type", false)]
    [InlineData("-type", "type", true)]
    [InlineData("--type", "name", false)]
    [InlineData("colour", "name", false)]
    [InlineData("-", "name", false)]
    public void A_sort_reads_only_the_sorts_on_offer(string text, string sort, bool descending)
    {
        var query = units.Read(name => name == ListDefinition.SortKey ? text : null);

        Assert.Equal((sort, descending), (query.Sort, query.Descending));
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("10000", 10000)]
    [InlineData("10001", 1)]
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("+3", 1)]
    [InlineData("3.0", 1)]
    [InlineData("٣", 1)]
    [InlineData("99999999999", 1)]
    public void A_page_reads_only_from_one_to_the_highest_page(string text, int page) =>
        Assert.Equal(page, units.Read(name => name == ListDefinition.PageKey ? text : null).Page);

    [Fact]
    public void Groupings_toggles_and_searches_the_list_does_not_offer_read_as_their_defaults()
    {
        Assert.Equal(OrganizationUnitList.ByParent, units.Read(name => name == ListDefinition.GroupKey ? "colour" : null).Group);
        Assert.Equal(OrganizationUnitList.ByType, units.Read(name => name == ListDefinition.GroupKey ? "TYPE" : null).Group);
        Assert.Null(units.Read(name => name == ListDefinition.GroupKey ? "None" : null).Group);
        Assert.Null(users.Read(name => name == ListDefinition.GroupKey ? "colour" : null).Group);

        Assert.True(units.Read(name => name == OrganizationUnitList.ShowInactive ? "TRUE" : null).IsOn(OrganizationUnitList.ShowInactive));
        Assert.False(units.Read(name => name == OrganizationUnitList.ShowInactive ? "yes" : null).IsOn(OrganizationUnitList.ShowInactive));
        Assert.False(units.Read(name => name == OrganizationUnitList.ShowInactive ? "false" : null).IsOn(OrganizationUnitList.ShowInactive));

        Assert.Equal("north", units.Read(name => name == ListDefinition.SearchKey ? "  north  " : null).Search);
        Assert.Equal(string.Empty, units.Read(name => name == ListDefinition.SearchKey ? new string('x', ListDefinition.MaxSearchLength + 1) : null).Search);
        Assert.Equal(new string('x', ListDefinition.MaxSearchLength), units.Read(name => name == ListDefinition.SearchKey ? new string('x', ListDefinition.MaxSearchLength) : null).Search);
    }

    [Fact]
    public void Normalize_keeps_a_callers_query_to_what_the_list_offers()
    {
        var crafted = new ListQuery
        {
            Search = "  " + new string('y', 150) + "  ",
            Filters = new Dictionary<string, string> { [OrganizationUnitList.Type] = "TEAM", ["tenant"] = "other", [UserList.Status] = UserList.Active },
            Toggles = new HashSet<string> { OrganizationUnitList.ShowInactive, "everything" },
            Group = "colour",
            Sort = "secret",
            Descending = true,
            Page = -4,
            PageSize = 5000,
        };

        var normalized = units.Normalize(crafted);

        Assert.Equal(new string('y', ListDefinition.MaxSearchLength), normalized.Search);
        Assert.Equal(new Dictionary<string, string> { [OrganizationUnitList.Type] = "team" }, normalized.Filters);
        Assert.Equal([OrganizationUnitList.ShowInactive], normalized.Toggles);
        Assert.Equal((OrganizationUnitList.ByParent, OrganizationUnitList.SortName, false, 1, ListDefinition.DefaultPageSize), (normalized.Group, normalized.Sort, normalized.Descending, normalized.Page, normalized.PageSize));
        Assert.Equal(units.Default, units.Normalize(null));
        Assert.Null(units.Normalize(units.Default with { Group = null }).Group);
    }

    [Fact]
    public void Equal_queries_are_equal_whatever_the_order_of_their_filters_and_toggles()
    {
        var one = new ListQuery { Filters = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, Toggles = new HashSet<string> { "x", "y" } };
        var two = new ListQuery { Filters = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" }, Toggles = new HashSet<string> { "y", "x" } };

        Assert.Equal(one, two);
        Assert.Equal(one.GetHashCode(), two.GetHashCode());
        Assert.NotEqual(one, two.WithFilter("a", "3"));
        Assert.NotEqual(one, two.WithToggle("x", false));
        Assert.NotEqual(one, two with { Page = 2 });
        Assert.Equal(one, one.WithFilter("c", "9").WithFilter("c", null));
    }

    [Fact]
    public void A_definition_that_could_not_keep_its_state_in_the_address_is_refused()
    {
        ListOption[] sorts = [new("name", "Name")];

        Assert.Throws<ArgumentException>(() => new ListDefinition(sorts, "size"));
        Assert.Throws<ArgumentException>(() => new ListDefinition(sorts, "name", filters: [new ListFilterDefinition("Status", "Status", [new("a", "A")])]));
        Assert.Throws<ArgumentException>(() => new ListDefinition(sorts, "name", filters: [new ListFilterDefinition("page", "Page", [new("a", "A")])]));
        Assert.Throws<ArgumentException>(() => new ListDefinition(sorts, "name", filters: [new ListFilterDefinition("dlg", "Dialogs", [new("a", "A")])]));
        Assert.Throws<ArgumentException>(() => new ListDefinition(sorts, "name", filters: [new ListFilterDefinition("kind", "Kind", [new("a", "A")])], toggles: [new ListToggleDefinition("kind", "Kind", "1", "{0}")]));
        Assert.Throws<ArgumentException>(() => new ListDefinition(sorts, "name", filters: [new ListFilterDefinition("kind", "Kind", [new("A b", "A")])]));
        Assert.Throws<ArgumentException>(() => new ListDefinition(sorts, "name", groups: [new("none", "None")]));
        Assert.Throws<ArgumentException>(() => new ListDefinition(sorts, "name", groups: [new("kind", "Kind")], defaultGroup: "size"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListDefinition(sorts, "name", pageSize: ListDefinition.MaxPageSize + 1));
    }

    [Fact]
    public void A_page_past_the_rows_is_read_as_the_last_page()
    {
        var query = new ListQuery { Page = 9, PageSize = 10 };

        Assert.Equal(3, ListPage<int>.PageWithin(query, 25));
        Assert.Equal(1, ListPage<int>.PageWithin(query, 0));
        Assert.Equal(2, ListPage<int>.PageWithin(query with { Page = 2 }, 25));
        Assert.Equal(3, ListPage<int>.Ungrouped([21, 22, 23, 24, 25], 25, 3, 10).PageCount);
        Assert.Equal(1, ListPage<int>.Ungrouped([], 0, 1, 10).PageCount);
    }

    [Fact]
    public void A_grouped_page_splits_its_rows_where_the_group_changes_and_counts_each_group_across_pages()
    {
        var page = ListPage<string>.Grouped(["a1", "a2", "b1", "c1"], 9, 1, 4, row => row[..1], new Dictionary<string, (string, int)>
        {
            ["a"] = ("Group A", 2),
            ["b"] = ("Group B", 5),
        });

        Assert.Equal(["a", "b", "c"], page.Sections.Select(section => section.Key));
        Assert.Equal(["Group A", "Group B", "c"], page.Sections.Select(section => section.Label));
        Assert.Equal([2, 5, 1], page.Sections.Select(section => section.TotalCount));
        Assert.Equal(["a1", "a2", "b1", "c1"], page.Rows);
        Assert.Empty(ListPage<string>.Grouped([], 0, 1, 4, row => row, new Dictionary<string, (string, int)>()).Sections);
    }

    [Fact]
    public void An_order_puts_each_key_after_the_ones_before_it()
    {
        var rows = new[] { (Group: 2, Name: "b"), (Group: 1, Name: "b"), (Group: 2, Name: "a"), (Group: 1, Name: "a") }.AsQueryable();

        var ordered = new ListOrder<(int Group, string Name)>(rows).By(row => row.Group).By(row => row.Name, descending: true).Query.ToList();

        Assert.Equal([(1, "b"), (1, "a"), (2, "b"), (2, "a")], ordered);
        Assert.Same(rows, new ListOrder<(int Group, string Name)>(rows).Query);
    }

    [Fact]
    public void A_query_without_filter_or_toggle_collections_normalizes_as_having_none()
    {
        var query = units.Default with { Filters = null!, Toggles = null!, Search = "north" };

        var normalized = units.Normalize(query);

        Assert.Empty(normalized.Filters);
        Assert.Empty(normalized.Toggles);
        Assert.Equal("north", normalized.Search);
        Assert.Null(query.Filter(OrganizationUnitList.Type));
        Assert.False(query.IsOn(OrganizationUnitList.ShowInactive));
        Assert.Equal(units.Default with { Search = "north" }, normalized);
    }

    [Fact]
    public void An_address_prefix_names_a_lists_keys_apart_and_is_refused_unless_it_is_a_plain_name()
    {
        Assert.Equal("q", ListDefinition.AddressName(null, ListDefinition.SearchKey));
        Assert.Equal("slots.q", ListDefinition.AddressName("slots", ListDefinition.SearchKey));
        Assert.Equal("slots.page", ListDefinition.AddressName("slots", ListDefinition.PageKey));
        Assert.All(units.AddressKeys, key => Assert.True(UrlStateKeys.IsValidName(ListDefinition.AddressName("units2", key)), key));

        foreach (var prefix in new[] { "", "Slots", "1st", "my-list", "a.b", "averyveryverylongname", ListDefinition.DialogStackKey })
        {
            Assert.Throws<ArgumentException>(() => ListDefinition.AddressName(prefix, ListDefinition.SearchKey));
        }
    }

    [Fact]
    public void Every_lists_address_keys_are_names_of_the_apps_one_vocabulary()
    {
        var vocabulary = typeof(UrlStateKeys).GetFields().Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!).ToHashSet();

        Assert.All(units.AddressKeys.Concat(users.AddressKeys), key => Assert.Contains(key, vocabulary));
    }

    [Fact]
    public void An_enum_filter_offers_every_member_by_its_lowercase_name_and_display_name()
    {
        var options = ListOption.ForEnum<OrganizationUnitType>();

        Assert.Equal(["company", "region", "branch", "team", "grouping"], options.Select(option => option.Value));
        Assert.Equal(["Company", "Region", "Branch", "Team", "Grouping"], options.Select(option => option.Label));
        Assert.False(ListOption.TryParseEnum<OrganizationUnitType>("2", out _));
        Assert.False(ListOption.TryParseEnum<OrganizationUnitType>("Team,Branch", out _));
        Assert.True(ListOption.TryParseEnum<OrganizationUnitType>("TEAM", out var team) && team == OrganizationUnitType.Team);
    }
}
