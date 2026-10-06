using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Walker.Core;
namespace Walker.Roslyn;
public sealed class RoslynMutationDiscoverer : IMutationDiscoverer
{
    private static readonly Lazy<MetadataReference[]> References = new(() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)).ToArray());
    public async Task<DiscoveryResult> DiscoverAsync(string root, IReadOnlyList<SourceChange> changes, CancellationToken cancellationToken)
    {
        var result = new List<Walker.Core.Mutant>();
        long parsing = 0, discovery = 0;
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await File.ReadAllTextAsync(Path.Combine(root, change.File), cancellationToken);
            var timer = Stopwatch.StartNew();
            var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
            var syntax = await tree.GetRootAsync(cancellationToken);
            parsing += timer.ElapsedMilliseconds;
            timer.Restart();
            // Most high-priority operators need no type information. Avoid loading metadata
            // and constructing a compilation unless a changed arithmetic expression needs it.
            SemanticModel? model = null;
            SemanticModel GetModel() => model ??= CSharpCompilation.Create("Analysis", [tree], References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetSemanticModel(tree);
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
                    BinaryExpressionSyntax b => Binary(b, GetModel, cancellationToken),
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
                var id = Walker.Core.Mutant.Hash($"{change.File}|{name}|{node.SpanStart}|{mutation.Value.Op}|{original}|{mutation.Value.Replacement}")[..20];
                result.Add(new(id, change.File, line, name, mutation.Value.Op, original, mutation.Value.Replacement,
                    node.SpanStart, node.Span.Length, sourceHash ??= Walker.Core.Mutant.Hash(source)));
            }
            discovery += timer.ElapsedMilliseconds;
        }
        return new(result, parsing, discovery);
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
    private static (MutationOperator, string)? Binary(BinaryExpressionSyntax node, Func<SemanticModel> getModel, CancellationToken token)
    {
        var op = node.OperatorToken.Text;
        string? replacement = op switch { ">" => ">=", ">=" => ">", "<" => "<=", "<=" => "<", "==" => "!=", "!=" => "==",
            "&&" => "||", "||" => "&&", "+" => "-", "-" => "+", "*" => "/", _ => null };
        if (replacement == null) return null;
        var kind = op switch { ">" or ">=" or "<" or "<=" => MutationOperator.ConditionalBoundary,
            "==" or "!=" => node.Left.IsKind(SyntaxKind.NullLiteralExpression) || node.Right.IsKind(SyntaxKind.NullLiteralExpression)
                ? MutationOperator.NullHandling : MutationOperator.Equality,
            "&&" or "||" => MutationOperator.BooleanLogic, _ => MutationOperator.Arithmetic };
        // Only mutate arithmetic with known built-in numeric operands; skip strings and unknown overloads.
        if (kind == MutationOperator.Arithmetic)
        {
            var model = getModel();
            if (!Numeric(model.GetTypeInfo(node.Left, token).Type) || !Numeric(model.GetTypeInfo(node.Right, token).Type)) return null;
        }
        var local = node.OperatorToken.SpanStart - node.SpanStart;
        var text = node.ToString();
        return (kind, text[..local] + replacement + text[(local + node.OperatorToken.Span.Length)..]);
    }
    private static bool Numeric(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;
}
