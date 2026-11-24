using Staffora.Core.Entities;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Services.Customers;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;

namespace Staffora.Test.Unit.Entities;

public class NameAndDescriptionRuleTests
{
    private static readonly NameAndDescriptionRule rule = new("work pattern", 10, 20);

    [Fact]
    public void A_name_is_required_and_both_texts_are_held_to_their_lengths()
    {
        Assert.Equal("A work pattern needs a name.", rule.Problem(string.Empty, null));
        Assert.Equal("A work pattern's name has at most 10 characters.", rule.Problem(new string('n', 11), null));
        Assert.Equal("A work pattern's description has at most 20 characters.", rule.Problem("Weekdays", new string('d', 21)));

        Assert.Null(rule.Problem(new string('n', 10), new string('d', 20)));
        Assert.Null(rule.Problem("W", null));
        Assert.Null(rule.Problem("W", string.Empty));
    }

    [Fact]
    public void The_name_is_judged_before_the_description()
    {
        Assert.Equal(rule.NameMissing, rule.Problem(string.Empty, new string('d', 21)));
        Assert.Equal(rule.NameTooLong, rule.Problem(new string('n', 11), new string('d', 21)));
    }

    [Fact]
    public void The_services_answer_in_the_words_they_used_before_over_their_entities_columns()
    {
        Assert.Equal(
            ("A work pattern needs a name.", $"A work pattern's name has at most {WorkPattern.NameMaxLength} characters.", $"A work pattern's description has at most {WorkPattern.DescriptionMaxLength} characters."),
            (WorkPatternService.TextRule.NameMissing, WorkPatternService.TextRule.NameTooLong, WorkPatternService.TextRule.DescriptionTooLong));
        Assert.Equal(
            ("A qualification needs a name.", $"A qualification's name has at most {Qualification.NameMaxLength} characters.", $"A qualification's description has at most {Qualification.DescriptionMaxLength} characters."),
            (QualificationService.TextRule.NameMissing, QualificationService.TextRule.NameTooLong, QualificationService.TextRule.DescriptionTooLong));
        Assert.Equal((WorkPattern.NameMaxLength, WorkPattern.DescriptionMaxLength), (WorkPatternService.TextRule.NameMaxLength, WorkPatternService.TextRule.DescriptionMaxLength));
        Assert.Equal((Qualification.NameMaxLength, Qualification.DescriptionMaxLength), (QualificationService.TextRule.NameMaxLength, QualificationService.TextRule.DescriptionMaxLength));
        Assert.Equal(
            ("A customer needs a name.", $"A customer's name has at most {Customer.NameMaxLength} characters.", $"A customer's description has at most {Customer.DescriptionMaxLength} characters."),
            (CustomerService.TextRule.NameMissing, CustomerService.TextRule.NameTooLong, CustomerService.TextRule.DescriptionTooLong));
        Assert.Equal((Customer.NameMaxLength, Customer.DescriptionMaxLength), (CustomerService.TextRule.NameMaxLength, CustomerService.TextRule.DescriptionMaxLength));
    }

    [Fact]
    public void A_rule_without_a_noun_is_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => new NameAndDescriptionRule(" ", 10, 20));
    }
}
