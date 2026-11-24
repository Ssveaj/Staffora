using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;

namespace Staffora.Test.Unit.EntityAttributes;

public class AttributeDefinitionRulesTests
{
    [Fact]
    public void A_field_needs_a_name_within_its_length_and_a_type_it_may_take()
    {
        Assert.Equal("A custom field needs a name.", AttributeDefinitionRules.DescribeInvalid(Field("   ", AttributeValueType.Text)));
        Assert.Equal($"A custom field's name has at most {AttributeDefinitionRules.NameMaxLength} characters.", AttributeDefinitionRules.DescribeInvalid(Field(new string('n', AttributeDefinitionRules.NameMaxLength + 1), AttributeValueType.Text)));
        Assert.Null(AttributeDefinitionRules.DescribeInvalid(Field(new string('n', AttributeDefinitionRules.NameMaxLength), AttributeValueType.Text)));

        Assert.DoesNotContain((AttributeValueType)5, AttributeDefinitionRules.Types);
        Assert.Equal("Choose the kind of value the field holds.", AttributeDefinitionRules.DescribeInvalid(Field("Settings", (AttributeValueType)5)));
    }

    [Fact]
    public void Every_kind_of_field_but_a_yes_or_no_may_be_required()
    {
        foreach (var type in AttributeDefinitionRules.Types.Where(type => type != AttributeValueType.Boolean))
        {
            var field = Field("Grade", type, type == AttributeValueType.Choice ? [Option("a", "A")] : []);
            field.IsRequired = true;
            Assert.Null(AttributeDefinitionRules.DescribeInvalid(field));
        }

        var yesOrNo = Field("Flagged", AttributeValueType.Boolean);
        yesOrNo.IsRequired = true;
        Assert.Equal(AttributeDefinitionRules.RequiredYesOrNo, AttributeDefinitionRules.DescribeInvalid(yesOrNo));
        yesOrNo.IsRequired = false;
        Assert.Null(AttributeDefinitionRules.DescribeInvalid(yesOrNo));
    }

    [Fact]
    public void A_saved_field_keeps_its_type()
    {
        var stored = Field("Badge", AttributeValueType.Text);

        Assert.Equal("A saved field keeps its type: Badge holds text values. Add a new field for another kind of value.", AttributeDefinitionRules.DescribeInvalid(Field("Badge", AttributeValueType.Integer), stored));
        Assert.Null(AttributeDefinitionRules.DescribeInvalid(Field("Badge number", AttributeValueType.Text), stored));
    }

    [Fact]
    public void Only_a_choice_has_options_and_it_needs_one_that_is_not_retired()
    {
        Assert.Equal("Only a choice field has options.", AttributeDefinitionRules.DescribeInvalid(Field("Badge", AttributeValueType.Text, Option("a", "A"))));
        Assert.Equal("A choice field needs at least one option that is not retired.", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice)));
        Assert.Equal("A choice field needs at least one option that is not retired.", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("a", "A", IsRetired: true))));
        Assert.Null(AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("a", "A", IsRetired: true), Option("b", "B"))));

        var tooMany = Enumerable.Range(1, AttributeChoiceOption.MaxOptions + 1).Select(n => new AttributeChoiceOption($"o{n}", $"Option {n}")).ToArray();
        Assert.Equal($"A choice field has at most {AttributeChoiceOption.MaxOptions} options.", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, tooMany)));
    }

    [Fact]
    public void Every_option_has_a_unique_label_and_a_well_formed_unique_key()
    {
        Assert.Equal("Every option needs a label.", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("a", " "))));
        Assert.Equal($"An option's label has at most {AttributeChoiceOption.LabelMaxLength} characters.", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("a", new string('l', AttributeChoiceOption.LabelMaxLength + 1)))));
        Assert.Equal("Two options are labelled \"red\".", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("a", "Red"), Option("b", "red"))));
        Assert.Equal("The option \"Red\" has no valid key.", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("Not a key", "Red"))));
        Assert.Equal("The option \"Red\" has no valid key.", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option(string.Empty, "Red"))));
        Assert.Equal("Two options share the key \"a\".", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("a", "Red"), Option("a", "Blue"))));
    }

    [Fact]
    public void A_saved_option_is_relabelled_or_retired_but_never_removed()
    {
        var stored = Field("Team", AttributeValueType.Choice, Option("teaching", "Working"), Option("planning", "Planning"));

        Assert.Equal("The option \"Planning\" cannot be removed, since values may hold it. Retire it instead.", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("teaching", "Working")), stored));
        Assert.Equal("The option \"Planning\" cannot be removed, since values may hold it. Retire it instead.", AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("teaching", "Working"), Option("planning-2", "Planning")), stored));
        Assert.Null(AttributeDefinitionRules.DescribeInvalid(Field("Team", AttributeValueType.Choice, Option("teaching", "Times"), Option("planning", "Planning", IsRetired: true)), stored));
    }

    [Fact]
    public void A_new_key_is_made_once_from_the_label_and_never_takes_one_the_field_holds()
    {
        Assert.Equal("year-7-to-9", AttributeChoiceOption.NewKey("Year 7 to 9", new HashSet<string>()));
        Assert.Equal("asa-orebro", AttributeChoiceOption.NewKey("Åsa  Örebro!", new HashSet<string>()));
        Assert.Equal("option", AttributeChoiceOption.NewKey("—", new HashSet<string>()));
        Assert.Equal("red-3", AttributeChoiceOption.NewKey("Red", new HashSet<string> { "red", "red-2" }));
        Assert.True(AttributeChoiceOption.IsValidKey(AttributeChoiceOption.NewKey(new string('x', 200), new HashSet<string>())));

        var stored = new[] { "red", "blue" };
        var keyed = AttributeChoiceOption.WithKeys(
            [new("red", "Crimson"), new("blue", "Blue", IsRetired: true), new(string.Empty, " Blue "), new(string.Empty, "Red")],
            stored);

        Assert.Equal([new("red", "Crimson"), new("blue", "Blue", IsRetired: true), new("blue-2", "Blue"), new("red-2", "Red")], keyed);
        Assert.All(keyed, option => Assert.True(AttributeChoiceOption.IsValidKey(option.Key), option.Key));

        Assert.Equal([new("red-2", "Red")], AttributeChoiceOption.WithKeys([Option(string.Empty, "Red")], stored));
    }

    [Fact]
    public void A_key_the_stored_field_does_not_hold_is_replaced_by_one_made_from_the_label()
    {
        var stored = new[] { "red", "blue" };

        Assert.Equal(
            [new("red", "Red"), new("green", "Green"), new("yellow", "Yellow"), new("red-again", "Red again")],
            AttributeChoiceOption.WithKeys([Option("red", "Red"), Option("made-up", "Green"), Option("blue-2", "Yellow"), Option("RED", "Red again")], stored));

        Assert.Equal([new("small", "Small"), new("large", "Large")], AttributeChoiceOption.WithKeys([Option("client-picked", "Small"), Option("x", "Large")], []));
    }

    [Theory]
    [InlineData("Straße", "strasse")]
    [InlineData("Ǆ ligature", "dz-ligature")]
    [InlineData("Ǆungla", "dzungla")]
    [InlineData("ﬁnance", "finance")]
    [InlineData("Café", "cafe")]
    [InlineData("Smørrebrød Æble", "smorrebrod-aeble")]
    [InlineData("Œuvre Łódź", "oeuvre-lodz")]
    [InlineData("日本語", "option")]
    [InlineData("!!!", "option")]
    public void A_new_key_spells_its_label_in_ascii(string label, string key)
    {
        Assert.Equal(key, AttributeChoiceOption.NewKey(label, new HashSet<string>()));
        Assert.True(AttributeChoiceOption.IsValidKey(key));
    }

    [Fact]
    public void A_label_with_nothing_to_spell_takes_the_next_free_option_key()
    {
        Assert.Equal("option-2", AttributeChoiceOption.NewKey("!!!", new HashSet<string> { "option" }));
        Assert.Equal("option-3", AttributeChoiceOption.NewKey("日本語", new HashSet<string> { "option", "option-2" }));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("year-7", true)]
    [InlineData("", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("a--b", false)]
    [InlineData("A", false)]
    [InlineData("a b", false)]
    [InlineData("ö", false)]
    public void A_key_is_lowercase_ascii_with_single_inner_hyphens(string key, bool valid)
    {
        Assert.Equal(valid, AttributeChoiceOption.IsValidKey(key));
    }

    private static AttributeChoiceOption Option(string key, string label, bool IsRetired = false) => new(key, label, IsRetired);

    private static ApplicationUserAttributeDefinition Field(string name, AttributeValueType type, params AttributeChoiceOption[] options) =>
        new() { Name = name, ValueType = type, Options = [.. options] };
}
