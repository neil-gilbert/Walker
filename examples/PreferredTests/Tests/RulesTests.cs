using PreferredFixture;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace PreferredFixture.Tests;

// Deliberate fixed work makes subset costs visible without relying on machine speed.
public sealed class RulesTests
{
    private readonly Rules rules = new();
    [Fact]
    public void A()
    {
        Thread.Sleep(500);
        Assert.Equal(4, rules.A(0, true, true, true));
        Assert.Equal(2, rules.A(0, false, true, true));
        Assert.Equal(2, rules.A(0, true, false, true));
        Assert.Equal(2, rules.A(0, true, true, false));
    }
    [Fact] public void B() { Thread.Sleep(500); Assert.True(rules.B(0)); }
    [Fact] public void C() { Thread.Sleep(500); Assert.True(rules.C(0)); }
    [Fact] public void D() { Thread.Sleep(500); Assert.True(rules.D(0)); }
    [Fact] public void UnrelatedWork() => Thread.Sleep(2000);
}
