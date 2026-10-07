using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;

namespace Walker.Tests;

public sealed class InstrumentationTests
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    [Fact]
    public async Task OriginalInactiveAndActiveBranchesEvaluateOperandsOnceInFreshProcesses()
    {
        using var workspace = new Workspace();
        const string source = """
            using System;
            public class Program {
                static int calls;
                static bool throwing;
                static int Next() { calls++; if (throwing) throw new InvalidOperationException(); return 1; }
                static bool Boundary() => Next() >= 1;
                public static void Main() {
                    Console.Write(Boundary() + ":" + calls);
                    Console.Write(":" + (false && Boundary()) + ":" + calls);
                    throwing = true;
                    try { Boundary(); } catch (InvalidOperationException error) { Console.Write(":" + error.GetType().Name + ":" + calls); }
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, path: "Code.cs");
        var compilation = CSharpCompilation.Create("Fixture", [tree], References, new(OutputKind.ConsoleApplication));
        var original = "Next() >= 1";
        var mutant = new Mutant("boundary", "Code.cs", 5, "Program.Boundary", MutationOperator.ConditionalBoundary, original, "Next() > 1", source.IndexOf(original, StringComparison.Ordinal), original.Length, Mutant.Hash(source));
        var instrumented = new MutationInstrumenter().Instrument(compilation, [mutant], "UniqueRuntime", "WALKER_TEST_ACTIVE");
        Assert.Equal(1, Assert.Single(instrumented.ActiveIds).Value);
        var rewritten = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(instrumented.Sources["Code.cs"]), CSharpSyntaxTree.ParseText(instrumented.RuntimeSource));
        var assembly = Path.Combine(workspace.Root, "Fixture.dll");
        var emission = rewritten.Emit(assembly);
        Assert.True(emission.Success, string.Join("\n", emission.Diagnostics));
        workspace.Write("Fixture.runtimeconfig.json", "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
        foreach (var (active, expected) in new[] { ("0", "True:1:False:1"), ("1", "False:1:False:1"), ("99", "True:1:False:1"), ("0", "True:1:False:1") })
        {
            var result = await new ProcessRunner().RunAsync(new("dotnet", [assembly], workspace.Root,
                Environment: new Dictionary<string, string?> { ["WALKER_TEST_ACTIVE"] = active }), default);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected + ":InvalidOperationException:2", result.StandardOutput);
        }
        Assert.Null(Environment.GetEnvironmentVariable("WALKER_TEST_ACTIVE"));
    }

    [Theory]
    [InlineData("using System.Linq.Expressions; class C { Expression<System.Func<int,bool>> A() => value => value >= 1; }", "value >= 1")]
    [InlineData("class C { const bool Value = 2 >= 1; }", "2 >= 1")]
    [InlineData("class C { bool A(int? value) => value >= 1; }", "value >= 1")]
    [InlineData("class C { bool A(dynamic value) => value >= 1; }", "value >= 1")]
    [InlineData("class C { bool A(Unknown value) => value >= 1; }", "value >= 1")]
    [InlineData("class C { bool A(decimal value) => value >= 1; }", "value >= 1")]
    [InlineData("class C { public static explicit operator int(C v) => 1; bool A(C value) => (int)value >= 1; }", "(int)value >= 1")]
    [InlineData("class C { public static bool operator >=(C v, int n) => true; public static bool operator <=(C v, int n) => true; bool A(C value) => value >= 1; }", "value >= 1")]
    [InlineData("class C { int Next(out int n) { n = 1; return 1; } bool A() => Next(out var n) >= 1; }", "Next(out var n) >= 1")]
    public void UnsupportedContextsRemainOrdinarySourceMutants(string source, string original)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Code.cs");
        var compilation = CSharpCompilation.Create("Fixture", [tree], References, new(OutputKind.DynamicallyLinkedLibrary));
        var candidate = new Mutant("unsupported", "Code.cs", 1, "C.A", MutationOperator.ConditionalBoundary,
            original, original.Replace(">=", ">", StringComparison.Ordinal), source.IndexOf(original, StringComparison.Ordinal), original.Length, Mutant.Hash(source));
        var result = new MutationInstrumenter().Instrument(compilation, [candidate], "UniqueRuntime", "WALKER_TEST_ACTIVE");
        Assert.Empty(result.ActiveIds);
        Assert.Empty(result.Sources);
    }
}
