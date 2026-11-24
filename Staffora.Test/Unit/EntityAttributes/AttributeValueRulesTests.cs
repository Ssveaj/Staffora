using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;

namespace Staffora.Test.Unit.EntityAttributes;

public class AttributeValueRulesTests
{
    private static readonly ApplicationUserAttributeDefinition team = new()
    {
        Id = Guid.NewGuid(),
        State = CustomFieldState.Active,
        Name = "Team",
        ValueType = AttributeValueType.Choice,
        Options = [new("teaching", "Working"), new("planning", "Planning"), new("support", "Support", IsRetired: true)],
    };

    [Theory]
    [InlineData(AttributeValueType.Text)]
    [InlineData(AttributeValueType.Integer)]
    [InlineData(AttributeValueType.Number)]
    [InlineData(AttributeValueType.Date)]
    [InlineData(AttributeValueType.Boolean)]
    [InlineData(AttributeValueType.Choice)]
    public void Each_type_takes_a_value_in_its_own_column_only(AttributeValueType type)
    {
        var field = Field(type);
        Assert.Null(AttributeValueRules.DescribeInvalid(field, new ApplicationUserAttribute()));
        Assert.Null(AttributeValueRules.DescribeInvalid(field, ValueOf(type)));

        foreach (var other in Enum.GetValues<AttributeValueType>().Where(other => ColumnOf(other) != ColumnOf(type)))
        {
            var reason = AttributeValueRules.DescribeInvalid(field, ValueOf(other));
            Assert.NotNull(reason);
            Assert.StartsWith($"{field.Name} holds ", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_text_is_bounded_by_its_column_and_a_number_is_finite()
    {
        var notes = Field(AttributeValueType.Text);
        Assert.Null(AttributeValueRules.DescribeInvalid(notes, new ApplicationUserAttribute { StringValue = new string('x', AttributeValueRules.TextMaxLength) }));
        Assert.Equal($"Text field holds at most {AttributeValueRules.TextMaxLength} characters.", AttributeValueRules.DescribeInvalid(notes, new ApplicationUserAttribute { StringValue = new string('x', AttributeValueRules.TextMaxLength + 1) }));

        var share = Field(AttributeValueType.Number);
        Assert.Null(AttributeValueRules.DescribeInvalid(share, new ApplicationUserAttribute { DoubleValue = 0.25 }));
        Assert.Equal("Number field holds a finite number.", AttributeValueRules.DescribeInvalid(share, new ApplicationUserAttribute { DoubleValue = double.NaN }));
        Assert.Equal("Number field holds a finite number.", AttributeValueRules.DescribeInvalid(share, new ApplicationUserAttribute { DoubleValue = double.PositiveInfinity }));
    }

    [Fact]
    public void A_choice_outside_its_options_is_refused_and_one_of_its_keys_passes()
    {
        Assert.Null(AttributeValueRules.DescribeInvalid(team, new ApplicationUserAttribute { StringValue = "teaching" }));

        Assert.Equal("Team has no such option.", AttributeValueRules.DescribeInvalid(team, new ApplicationUserAttribute { StringValue = "marketing" }));
        Assert.Equal("Team has no such option.", AttributeValueRules.DescribeInvalid(team, new ApplicationUserAttribute { StringValue = "Working" }));
        Assert.Equal("Team has no such option.", AttributeValueRules.DescribeInvalid(team, new ApplicationUserAttribute { StringValue = "WORKING" }));
    }

    [Fact]
    public void A_retired_option_stays_where_it_was_chosen_but_is_never_chosen_anew()
    {
        var chosen = new ApplicationUserAttribute { StringValue = "support" };

        Assert.Equal("Team's option \"Support\" is retired. Choose another.", AttributeValueRules.DescribeInvalid(team, chosen));
        Assert.Equal("Team's option \"Support\" is retired. Choose another.", AttributeValueRules.DescribeInvalid(team, chosen, stored: new ApplicationUserAttribute { StringValue = "teaching" }));
        Assert.Null(AttributeValueRules.DescribeInvalid(team, chosen, stored: new ApplicationUserAttribute { StringValue = "support" }));
    }

    [Fact]
    public void A_field_whose_type_takes_no_values_takes_none()
    {
        var retired = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = "Settings", ValueType = (AttributeValueType)5 };

        Assert.Equal("Settings has a type that takes no values.", AttributeValueRules.DescribeInvalid(retired, new ApplicationUserAttribute { StringValue = "{\"a\":1}" }));
        Assert.Null(AttributeValueRules.DescribeInvalid(retired, new ApplicationUserAttribute()));
    }

    [Fact]
    public void A_value_set_names_only_fields_that_apply_each_once()
    {
        var applies = new Dictionary<Guid, IEntityAttributeDefinition> { [team.Id] = team };

        Assert.Null(AttributeValueRules.DescribeInvalidValues([new ApplicationUserAttribute { EntityAttributeDefinitionId = team.Id, StringValue = "planning" }], applies));
        Assert.Equal(AttributeValueRules.UnknownField, AttributeValueRules.DescribeInvalidValues([new ApplicationUserAttribute { EntityAttributeDefinitionId = Guid.NewGuid(), StringValue = "planning" }], applies));
        Assert.Equal(AttributeValueRules.DuplicateField, AttributeValueRules.DescribeInvalidValues(
            [new ApplicationUserAttribute { EntityAttributeDefinitionId = team.Id }, new ApplicationUserAttribute { EntityAttributeDefinitionId = team.Id, StringValue = "planning" }], applies));

        var stored = new Dictionary<Guid, ApplicationUserAttribute> { [team.Id] = new() { EntityAttributeDefinitionId = team.Id, StringValue = "support" } };
        Assert.Null(AttributeValueRules.DescribeInvalidValues([new ApplicationUserAttribute { EntityAttributeDefinitionId = team.Id, StringValue = "support" }], applies, stored));
        Assert.NotNull(AttributeValueRules.DescribeInvalidValues([new ApplicationUserAttribute { EntityAttributeDefinitionId = team.Id, StringValue = "support" }], applies));
    }

    [Fact]
    public void The_value_helpers_copy_compare_and_tell_an_empty_value_for_every_column()
    {
        var full = new OrganizationUnitAttribute { StringValue = "a", Int32Value = 1, DoubleValue = 1.5, BooleanValue = false, DateValue = new DateOnly(2026, 11, 3) };
        var copy = new OrganizationUnitAttribute();
        Assert.True(AttributeValues.IsEmpty(copy));

        AttributeValues.CopyValues(full, copy);
        Assert.True(AttributeValues.HaveSameValues(full, copy));
        Assert.False(AttributeValues.IsEmpty(copy));

        foreach (var change in new Action<OrganizationUnitAttribute>[] { v => v.StringValue = "b", v => v.Int32Value = 2, v => v.DoubleValue = 2.5, v => v.BooleanValue = true, v => v.DateValue = new DateOnly(2026, 11, 4) })
        {
            var changed = new OrganizationUnitAttribute();
            AttributeValues.CopyValues(full, changed);
            change(changed);
            Assert.False(AttributeValues.HaveSameValues(full, changed));
        }
    }

    [Fact]
    public void A_text_of_white_space_alone_is_normalized_to_no_value()
    {
        foreach (var blank in new[] { string.Empty, " ", "\t \n" })
        {
            var value = new ApplicationUserAttribute { StringValue = blank };
            Assert.True(AttributeValues.IsBlank(value));
            AttributeValues.Normalize(value);
            Assert.Null(value.StringValue);
            Assert.True(AttributeValues.IsEmpty(value));
        }

        var text = new ApplicationUserAttribute { StringValue = "  Left-handed " };
        AttributeValues.Normalize(text);
        Assert.Equal("  Left-handed ", text.StringValue);
        Assert.False(AttributeValues.IsBlank(text));

        var number = new ApplicationUserAttribute { Int32Value = 0, StringValue = " " };
        Assert.False(AttributeValues.IsBlank(number));
        AttributeValues.Normalize(number);
        Assert.Equal(((string?)null, (int?)0), (number.StringValue, number.Int32Value));
    }

    private static ApplicationUserAttributeDefinition Field(AttributeValueType type) => type == AttributeValueType.Choice
        ? team
        : new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Id = Guid.NewGuid(), Name = $"{AttributeValueRules.TypeLabel(type)} field", ValueType = type };

    private static ApplicationUserAttribute ValueOf(AttributeValueType type) => type switch
    {
        AttributeValueType.Text => new() { StringValue = "Text" },
        AttributeValueType.Choice => new() { StringValue = "teaching" },
        AttributeValueType.Integer => new() { Int32Value = 7 },
        AttributeValueType.Number => new() { DoubleValue = 7.5 },
        AttributeValueType.Date => new() { DateValue = new DateOnly(2026, 11, 3) },
        _ => new() { BooleanValue = true },
    };

    private static string ColumnOf(AttributeValueType type) => type switch
    {
        AttributeValueType.Text or AttributeValueType.Choice => nameof(IEntityAttributeValue.StringValue),
        _ => type.ToString(),
    };
    [Fact]
    public void A_value_of_a_field_not_in_use_is_kept_as_it_is_and_takes_nothing_new()
    {
        var inactive = new ApplicationUserAttributeDefinition { Id = Guid.NewGuid(), Name = "Locker", ValueType = AttributeValueType.Text, State = CustomFieldState.Inactive };
        var definitions = new Dictionary<Guid, IEntityAttributeDefinition> { [inactive.Id] = inactive };
        var kept = new ApplicationUserAttribute { EntityAttributeDefinitionId = inactive.Id, StringValue = "L-12" };

        Assert.Null(AttributeValueRules.DescribeInvalidValues([kept], definitions, new Dictionary<Guid, ApplicationUserAttribute> { [inactive.Id] = new() { EntityAttributeDefinitionId = inactive.Id, StringValue = "L-12" } }));
        Assert.Equal(AttributeValueRules.FieldNotInUse("Locker"), AttributeValueRules.DescribeInvalidValues([kept], definitions, new Dictionary<Guid, ApplicationUserAttribute> { [inactive.Id] = new() { EntityAttributeDefinitionId = inactive.Id, StringValue = "L-11" } }));
        Assert.Equal(AttributeValueRules.FieldNotInUse("Locker"), AttributeValueRules.DescribeInvalidValues([kept], definitions));

        inactive.State = CustomFieldState.Draft;
        Assert.Equal(AttributeValueRules.FieldNotInUse("Locker"), AttributeValueRules.DescribeInvalidValues([kept], definitions));
        inactive.State = CustomFieldState.Active;
        Assert.Null(AttributeValueRules.DescribeInvalidValues([kept], definitions));
    }
}
