using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Walker.Core;
namespace Walker.Roslyn;
// contextSources: the production project's Compile items, used only when arithmetic needs operand types.
public sealed class RoslynMutationDiscoverer(Func<CancellationToken, Task<IEnumerable<string>>>? contextSources = null) : IMutationDiscoverer
{
    private static readonly Lazy<MetadataReference[]> References = new(() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)).ToArray());
    // SDK implicit usings come from a build-generated file that Compile-item evaluation does not list.
    // An extra using can only make a name ambiguous (then skipped), never bind a name the project cannot.
    private static readonly SyntaxTree ImplicitUsings = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; "
        + "global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;");
    public async Task<DiscoveryResult> DiscoverAsync(string root, IReadOnlyList<SourceChange> changes, CancellationToken cancellationToken)
    {
        var result = new List<(int Order, Walker.Core.Mutant Mutant)>();
        // Arithmetic needs type information. Defer it so one compilation serves every changed file,
        // and nothing is loaded unless a changed arithmetic expression exists.
        var pending = new List<(int Order, SyntaxTree Tree, BinaryExpressionSyntax Node, Func<Walker.Core.Mutant> Create)>();
        var trees = new List<SyntaxTree>();
        long parsing = 0, discovery = 0;
        for (var order = 0; order < changes.Count; order++)
        {
            var change = changes[order];
            cancellationToken.ThrowIfCancellationRequested();
            var source = await File.ReadAllTextAsync(Path.Combine(root, change.File), cancellationToken);
            var timer = Stopwatch.StartNew();
            var tree = CSharpSyntaxTree.ParseText(source, path: Path.GetFullPath(change.File, root), cancellationToken: cancellationToken);
            var syntax = await tree.GetRootAsync(cancellationToken);
            trees.Add(tree);
            parsing += timer.ElapsedMilliseconds;
            timer.Restart();
            var regions = ChangedSpans(await tree.GetTextAsync(cancellationToken), change.Lines);
            string? sourceHash = null;
            foreach (var node in syntax.DescendantNodes(n => Intersects(regions, n.FullSpan)).OfType<ExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node is not (BinaryExpressionSyntax or LiteralExpressionSyntax or IsPatternExpressionSyntax)
                    || !Intersects(regions, node.Span)) continue;
                var line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
                var member = node.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or LocalFunctionStatementSyntax);
                if (member == null) continue;
                (MutationOperator Op, string Replacement)? mutation = node switch
                {
                    BinaryExpressionSyntax b => Binary(b),
                    LiteralExpressionSyntax l when l.IsKind(SyntaxKind.TrueLiteralExpression) => (ReturnOrBoolean(l), "false"),
                    LiteralExpressionSyntax l when l.IsKind(SyntaxKind.FalseLiteralExpression) => (ReturnOrBoolean(l), "true"),
                    IsPatternExpressionSyntax p when p.Pattern is ConstantPatternSyntax c && c.Expression.IsKind(SyntaxKind.NullLiteralExpression)
                        => (MutationOperator.NullHandling, p.Expression + " is not null"),
                    IsPatternExpressionSyntax p when p.Pattern is UnaryPatternSyntax u && u.IsKind(SyntaxKind.NotPattern)
                        && u.Pattern is ConstantPatternSyntax c && c.Expression.IsKind(SyntaxKind.NullLiteralExpression)
                        => (MutationOperator.NullHandling, p.Expression + " is null"),
                    _ => null
                };
                if (mutation == null) continue;
                var original = node.ToString();
                var name = string.Join(".", node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text)
                    .Append(member switch { MethodDeclarationSyntax m => m.Identifier.Text, ConstructorDeclarationSyntax c => c.Identifier.Text,
                        PropertyDeclarationSyntax p => p.Identifier.Text, LocalFunctionStatementSyntax l => l.Identifier.Text, _ => member.Kind().ToString() }));
                var (op, replacement) = mutation.Value;
                Walker.Core.Mutant Create()
                {
                    var id = Walker.Core.Mutant.Hash($"{change.File}|{name}|{node.SpanStart}|{op}|{original}|{replacement}")[..20];
                    return new(id, change.File, line, name, op, original, replacement, node.SpanStart, node.Span.Length, sourceHash ??= Walker.Core.Mutant.Hash(source));
                }
                if (op == MutationOperator.Arithmetic) pending.Add((order, tree, (BinaryExpressionSyntax)node, Create));
                else result.Add((order, Create()));
            }
            discovery += timer.ElapsedMilliseconds;
        }
        var unresolved = 0;
        if (pending.Count > 0)
        {
            var timer = Stopwatch.StartNew();
            var compilation = await CreateCompilationAsync(root, trees, cancellationToken);
            var models = new Dictionary<SyntaxTree, SemanticModel>();
            foreach (var (order, tree, node, create) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!models.TryGetValue(tree, out var model)) models[tree] = model = compilation.GetSemanticModel(tree);
                // Only mutate arithmetic with known built-in numeric operands; skip strings and unknown overloads.
                var left = model.GetTypeInfo(node.Left, cancellationToken).Type;
                var right = model.GetTypeInfo(node.Right, cancellationToken).Type;
                if (Numeric(left) && Numeric(right)) result.Add((order, create()));
                else if (Unknown(left) || Unknown(right)) unresolved++;
            }
            discovery += timer.ElapsedMilliseconds;
        }
        return new(result.OrderBy(r => r.Order).ThenBy(r => r.Mutant.SpanStart).Select(r => r.Mutant).ToList(), parsing, discovery, unresolved);
    }
    private async Task<CSharpCompilation> CreateCompilationAsync(string root, IReadOnlyList<SyntaxTree> changed, CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var included = changed.Select(t => t.FilePath).ToHashSet(comparer);
        var trees = new List<SyntaxTree>(changed) { ImplicitUsings };
        foreach (var path in contextSources == null ? [] : await contextSources(cancellationToken))
        {
            if (!included.Add(Path.GetFullPath(path, root)) || !File.Exists(path)) continue;
            trees.Add(CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(path, cancellationToken), path: path, cancellationToken: cancellationToken));
        }
        return CSharpCompilation.Create("Analysis", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }
    private static TextSpan[] ChangedSpans(SourceText text, IReadOnlyList<LineRange> ranges)
    {
        var spans = new List<TextSpan>();
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (range.Start < 1 || range.Start > text.Lines.Count || range.End < range.Start) continue;
            var start = text.Lines[range.Start - 1].Start;
            var end = text.Lines[Math.Min(range.End, text.Lines.Count) - 1].EndIncludingLineBreak;
            if (spans.Count > 0 && start <= spans[^1].End)
                spans[^1] = TextSpan.FromBounds(spans[^1].Start, Math.Max(spans[^1].End, end));
            else spans.Add(TextSpan.FromBounds(start, end));
        }
        return spans.ToArray();
    }
    private static bool Intersects(TextSpan[] regions, TextSpan span)
    {
        var low = 0; var high = regions.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (regions[middle].End <= span.Start) low = middle + 1;
            else if (regions[middle].Start >= span.End) high = middle - 1;
            else return true;
        }
        return false;
    }
    private static MutationOperator ReturnOrBoolean(SyntaxNode node) => node.Parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax
        ? MutationOperator.ReturnValue : MutationOperator.BooleanLogic;
    private static (MutationOperator, string)? Binary(BinaryExpressionSyntax node)
    {
        var op = node.OperatorToken.Text;
        string? replacement = op switch { ">" => ">=", ">=" => ">", "<" => "<=", "<=" => "<", "==" => "!=", "!=" => "==",
            "&&" => "||", "||" => "&&", "+" => "-", "-" => "+", "*" => "/", _ => null };
        if (replacement == null) return null;
        var kind = op switch { ">" or ">=" or "<" or "<=" => MutationOperator.ConditionalBoundary,
            "==" or "!=" => node.Left.IsKind(SyntaxKind.NullLiteralExpression) || node.Right.IsKind(SyntaxKind.NullLiteralExpression)
                ? MutationOperator.NullHandling : MutationOperator.Equality,
            "&&" or "||" => MutationOperator.BooleanLogic, _ => MutationOperator.Arithmetic };
        var local = node.OperatorToken.SpanStart - node.SpanStart;
        var text = node.ToString();
        return (kind, text[..local] + replacement + text[(local + node.OperatorToken.Span.Length)..]);
    }
    private static bool Unknown(ITypeSymbol? type) => type is null or { TypeKind: TypeKind.Error };
    private static bool Numeric(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;
}
