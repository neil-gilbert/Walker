using Xunit;
namespace PreparedFixture;

public class BoundaryTests
{
    private static int runs;
    [Fact] public void FreshHost() => Assert.Equal(1, ++runs);
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void Equality(int index)
    {
        Func<int, bool>[] methods = [
            Rules.A01,
            Rules.A02,
            Rules.A03,
            Rules.A04,
            Rules.A05,
            Rules.A06,
            Rules.A07,
            Rules.A08,
            Rules.A09,
            Rules.A10,
            Rules.A11,
            Rules.A12,
            Rules.A13,
            Rules.A14,
            Rules.A15,
            Rules.A16,
            Rules.A17,
            Rules.A18,
            Rules.A19,
            Rules.A20
        ];
        Assert.True(methods[index - 1](0));
    }
}
