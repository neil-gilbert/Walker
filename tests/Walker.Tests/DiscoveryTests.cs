using Walker.Core;
using Walker.Roslyn;
using Xunit;
namespace Walker.Tests;
public sealed class DiscoveryTests
{
    [Fact]
    public async Task OnlyChangedExpressionIsMutatedAndIdsAreStable()
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", "class Payment {\n public bool CanPurchase(decimal balance, decimal price) { return balance >= price; }\n public bool Unchanged(int x) { return x > 0; }\n}");
        var change = new SourceChange("Code.cs", [new(2, 2)], true);
        var discoverer = new RoslynMutationDiscoverer();
        var first = await discoverer.DiscoverAsync(workspace.Root, [change], default);
        var second = await discoverer.DiscoverAsync(workspace.Root, [change], default);
        var mutant = Assert.Single(first.Mutants);
        Assert.Equal(MutationOperator.ConditionalBoundary, mutant.Operator);
        Assert.Equal("balance >= price", mutant.Original);
        Assert.Equal("balance > price", mutant.Replacement);
        Assert.Equal("Payment.CanPurchase", mutant.Member);
        Assert.Equal(mutant.Id, Assert.Single(second.Mutants).Id);
    }
    [Theory]
    [InlineData("x > 0", "x >= 0")]
    [InlineData("x >= 0", "x > 0")]
    [InlineData("x < 0", "x <= 0")]
    [InlineData("x <= 0", "x < 0")]
    [InlineData("x == 0", "x != 0")]
    [InlineData("x != 0", "x == 0")]
    [InlineData("s is null", "s is not null")]
    [InlineData("s is not null", "s is null")]
    [InlineData("s == null", "s != null")]
    [InlineData("s != null", "s == null")]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    public async Task SupportsSimpleOperators(string expression, string replacement)
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", $"class C {{ bool M(int x, string s) => {expression}; }}");
        var found = await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root, [new("Code.cs", [new(1, 1)], false)], default);
        Assert.Contains(found.Mutants, m => m.Original == expression && m.Replacement == replacement);
    }
    [Fact]
    public async Task SkipsStringConcatenationButMutatesNumericArithmetic()
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", "class C { string S(string a, string b) => a + b; int N(int a, int b) => a + b; }");
        var found = await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root, [new("Code.cs", [new(1, 1)], false)], default);
        Assert.Equal("C.N", Assert.Single(found.Mutants).Member);
    }
    [Fact]
    public void SelectionIsBoundedAndDeterministic()
    {
        var arithmetic = Dummy("a", MutationOperator.Arithmetic);
        var boundary = Dummy("b", MutationOperator.ConditionalBoundary);
        Assert.Equal([boundary], VerificationEngine.Select([arithmetic, boundary], 1));
        Assert.Equal(VerificationEngine.Select([arithmetic, boundary], 2), VerificationEngine.Select([boundary, arithmetic], 2));
    }
    internal static Walker.Core.Mutant Dummy(string id, MutationOperator op = MutationOperator.Equality) => new(id, "Code.cs", 1, "C.M", op, "a == b", "a != b", 0, 6, "hash");
}
internal sealed class Workspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "walker-tests-" + Guid.NewGuid().ToString("N"));
    public Workspace() => Directory.CreateDirectory(Root);
    public void Write(string name, string source) { var path = Path.Combine(Root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, source); }
    public void Dispose() => Directory.Delete(Root, true);
}
