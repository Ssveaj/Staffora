using Staffora.Data;

namespace Staffora.Test.Unit.Data;

public class RequiredValueTests
{
    [Fact]
    public void Null_a_blank_text_and_the_empty_id_are_no_value_and_anything_else_is_one()
    {
        Assert.True(ApplicationDbContext.IsEmpty(null));
        Assert.True(ApplicationDbContext.IsEmpty("   "));
        Assert.True(ApplicationDbContext.IsEmpty(Guid.Empty));

        Assert.False(ApplicationDbContext.IsEmpty("A name"));
        Assert.False(ApplicationDbContext.IsEmpty(Guid.NewGuid()));
        Assert.False(ApplicationDbContext.IsEmpty(0));
        Assert.False(ApplicationDbContext.IsEmpty(false));
    }
}
