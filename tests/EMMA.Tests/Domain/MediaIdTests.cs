using EMMA.Domain;
using Xunit;

namespace EMMA.Tests.Domain;

public class MediaIdTests
{
    [Fact]
    public void Create_Throws_OnEmpty()
    {
        Assert.Throws<ArgumentException>(() => MediaId.Create(""));
        Assert.Throws<ArgumentException>(() => MediaId.Create("   "));
    }

    [Fact]
    public void Create_Trims_Value()
    {
        var id = MediaId.Create("  demo-1  ");
        Assert.Equal("demo-1", id.Value);
    }
}
