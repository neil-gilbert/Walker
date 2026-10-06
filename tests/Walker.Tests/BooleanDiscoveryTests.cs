using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Walker.Core;
using Walker.Roslyn;
using Xunit;
namespace Walker.Tests;

public sealed class BooleanDiscoveryTests
{
    [Theory]
    [InlineData("void M(string paymentMethod) { if (UsesChoiceIdAsIssuer(paymentMethod)) { Consume(); } }", "UsesChoiceIdAsIssuer(paymentMethod)", "!(UsesChoiceIdAsIssuer(paymentMethod))")]
    [InlineData("void M(string paymentMethod) { while (UsesChoiceIdAsIssuer(paymentMethod)) { Consume(); } }", "UsesChoiceIdAsIssuer(paymentMethod)", "!(UsesChoiceIdAsIssuer(paymentMethod))")]
    [InlineData("string M(string paymentMethod) => UsesChoiceIdAsIssuer(paymentMethod) ? paymentMethod.Trim() : paymentMethod.ToLower();", "UsesChoiceIdAsIssuer(paymentMethod)", "!(UsesChoiceIdAsIssuer(paymentMethod))")]
    [InlineData("bool M(string paymentMethod) => string.Equals(paymentMethod, EPS, StringComparison.OrdinalIgnoreCase);", "string.Equals(paymentMethod, EPS, StringComparison.OrdinalIgnoreCase)", "!(string.Equals(paymentMethod, EPS, StringComparison.OrdinalIgnoreCase))")]
    [InlineData("bool M(string paymentMethod) { return string.Equals(paymentMethod, EPS, StringComparison.OrdinalIgnoreCase); }", "string.Equals(paymentMethod, EPS, StringComparison.OrdinalIgnoreCase)", "!(string.Equals(paymentMethod, EPS, StringComparison.OrdinalIgnoreCase))")]
    [InlineData("bool M(Dto data) => data is { PaymentMethod: EPS, Tenant: { } };", "data is { PaymentMethod: EPS, Tenant: { } }", "data is not ({ PaymentMethod: EPS, Tenant: { } })")]
    [InlineData("bool M(object data) => data is string;", "data is string", "data is not (string)")]
    [InlineData("bool M(object data) => data is not string;", "data is not string", "data is not (not string)")]
    [InlineData("bool Enabled => string.Equals(EPS, EPS);", "string.Equals(EPS, EPS)", "!(string.Equals(EPS, EPS))")]
    public async Task GeneratesSingleValidNegationForChangedShape(string member, string original, string replacement)
    {
        var (source, found) = await Discover(member);
        var mutant = Assert.Single(found.Mutants);
        Assert.Equal(MutationOperator.BooleanLogic, mutant.Operator);
        Assert.Equal(original, mutant.Original);
        Assert.Equal(replacement, mutant.Replacement);
        AssertCompiles(source[..mutant.SpanStart] + mutant.Replacement + source[(mutant.SpanStart + mutant.SpanLength)..]);
    }
    [Theory]
    [InlineData("bool M(Dto data) => data is { PaymentMethod: EPS, Tenant: { } tenant };", 0)]
    [InlineData("void M(Dto data) { if (data is { PaymentMethod: EPS, Tenant: { } tenant }) { Console.WriteLine(tenant); } }", 0)]
    [InlineData("void M(string text) { if (int.TryParse(text, out var number)) { Console.WriteLine(number); } }", 0)]
    [InlineData("bool M() => MissingCall();", 1)]
    [InlineData("string M(string value) => value.Trim();", 0)]
    [InlineData("bool? M(bool? value) => value;", 0)]
    public async Task SkipsUnsafeUnresolvedOrNonBooleanBodies(string member, int unresolved)
    {
        var (_, found) = await Discover(member);
        Assert.Empty(found.Mutants);
        Assert.Equal(unresolved, found.UnresolvedBoolean);
    }
    [Theory]
    [InlineData("void M(int x) { if (x >= 0) Consume(); }", MutationOperator.ConditionalBoundary)]
    [InlineData("bool M(int x) => (x >= 0);", MutationOperator.ConditionalBoundary)]
    [InlineData("void M(string text) { if (text is null) Consume(); }", MutationOperator.NullHandling)]
    [InlineData("bool M() => true;", MutationOperator.ReturnValue)]
    [InlineData("void M(bool a, bool b) { if (a && b) Consume(); }", MutationOperator.BooleanLogic)]
    public async Task ExistingOperatorWinsOverBroadNegation(string member, MutationOperator expected)
    {
        var (_, found) = await Discover(member);
        Assert.Equal(expected, Assert.Single(found.Mutants).Operator);
    }
    [Fact]
    public async Task OverlappingCandidatesPreferBoundariesAndNeverOverlap()
    {
        var (source, found) = await Discover("void M(int a, int b) { if (a >= 0 && b < 10) Consume(); }");
        Assert.Equal(2, found.Mutants.Count);
        Assert.All(found.Mutants, mutant => Assert.Equal(MutationOperator.ConditionalBoundary, mutant.Operator));
        var spans = found.Mutants.OrderBy(m => m.SpanStart).ToArray();
        Assert.True(spans[0].SpanStart + spans[0].SpanLength <= spans[1].SpanStart);
        foreach (var mutant in spans)
            AssertCompiles(source[..mutant.SpanStart] + mutant.Replacement + source[(mutant.SpanStart + mutant.SpanLength)..]);
    }
    [Fact]
    public async Task NegationRequiresChangedExpressionNotMerelyChangedMember()
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", "using System; class C {\n bool M(string x) =>\n string.Equals(x, \"EPS\");\n}\n");
        var found = await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
            [new("Code.cs", [new(4, 4)], false)], default);
        Assert.Empty(found.Mutants);
    }
    private static async Task<(string Source, DiscoveryResult Found)> Discover(string member)
    {
        using var workspace = new Workspace();
        var source = "using System; class Dto { public string PaymentMethod = \"EPS\"; public object Tenant = new(); }\n"
            + "class C { const string EPS = \"EPS\"; void Consume() {} bool UsesChoiceIdAsIssuer(string method) => string.Equals(method, EPS);\n"
            + member + "\n}";
        workspace.Write("Code.cs", source);
        return (source, await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
            [new("Code.cs", [new(3, 3)], false)], default));
    }
    private static void AssertCompiles(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Shape", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }
}
