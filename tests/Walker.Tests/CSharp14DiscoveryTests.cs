using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Walker.Core;
using Walker.Roslyn;
using Xunit;

namespace Walker.Tests;

public sealed class CSharp14DiscoveryTests
{
    [Theory]
    [InlineData("public class C { public int Count { get => field + 1; set; } }",
        "field + 1", "field - 1", MutationOperator.Arithmetic)]
    [InlineData("public static class Extensions { extension(string text) { public bool HasValue() => !string.IsNullOrEmpty(text); } }",
        "!string.IsNullOrEmpty(text)", "!(!string.IsNullOrEmpty(text))", MutationOperator.BooleanLogic)]
    [InlineData("public static class Extensions { extension(int number) { public bool IsPositive => number >= 0; } }",
        "number >= 0", "number > 0", MutationOperator.ConditionalBoundary)]
    [InlineData("public class C { public bool Enabled { get; set; } public void Enable(C value) { value?.Enabled = true; } }",
        "true", "false", MutationOperator.BooleanLogic)]
    public async Task DiscoversCompilableMutationsInCSharp14(string source, string original, string replacement, MutationOperator kind)
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", source);
        var discoverer = new RoslynMutationDiscoverer();
        var changes = new SourceChange[] { new("Code.cs", [new(1, 1)], false) };
        var discovered = await discoverer.DiscoverAsync(workspace.Root, changes, default);
        var mutant = Assert.Single(discovered.Mutants);
        Assert.Equal(original, mutant.Original);
        Assert.Equal(replacement, mutant.Replacement);
        Assert.Equal(kind, mutant.Operator);
        Assert.Equal(0, discovered.UnresolvedArithmetic);
        Assert.Equal(0, discovered.UnresolvedBoolean);
        Assert.Equal(original, source.Substring(mutant.SpanStart, mutant.SpanLength));
        Assert.Equal(mutant.Id, Assert.Single((await discoverer.DiscoverAsync(workspace.Root, changes, default)).Mutants).Id);

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        foreach (var candidate in new[] { source, source[..mutant.SpanStart] + replacement + source[(mutant.SpanStart + mutant.SpanLength)..] })
        {
            var compilation = CSharpCompilation.Create("CSharp14", [CSharpSyntaxTree.ParseText(candidate)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }
    }
}
