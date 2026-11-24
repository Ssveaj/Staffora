using Staffora.Test.E2E;

namespace Staffora.Test.Unit.Testing;

public class E2EDatabaseVariableTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    public void A_missing_connection_fails_in_ci(string ciValue)
    {
        Assert.True(E2EDatabase.MustFailWhenUnset(ciValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("false")]
    [InlineData("0")]
    public void A_missing_connection_skips_outside_ci(string? ciValue)
    {
        Assert.False(E2EDatabase.MustFailWhenUnset(ciValue));
    }
}
