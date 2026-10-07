using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Walker.Core;

namespace Walker.Roslyn;

public sealed record InstrumentationResult(IReadOnlyDictionary<string, string> Sources,
    IReadOnlyDictionary<string, int> ActiveIds, string RuntimeSource);

public sealed class MutationInstrumenter
{
    public InstrumentationResult Instrument(CSharpCompilation compilation, IReadOnlyList<Mutant> selected,
        string runtimeNamespace, string environmentName, CancellationToken token = default)
    {
        if (!SyntaxFacts.IsValidIdentifier(runtimeNamespace)) throw new ArgumentException("Invalid generated namespace.", nameof(runtimeNamespace));
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            token.ThrowIfCancellationRequested();
            var root = tree.GetRoot(token);
            var source = tree.GetText(token).ToString();
            var candidates = selected.Where(m => m.File == tree.FilePath || Path.GetFullPath(m.File) == Path.GetFullPath(tree.FilePath)).ToArray();
            var model = compilation.GetSemanticModel(tree);
            var replacements = new Dictionary<SyntaxNode, ExpressionSyntax>();
            foreach (var mutant in candidates)
            {
                if (mutant.Operator != MutationOperator.ConditionalBoundary || Mutant.Hash(source) != mutant.SourceHash
                    || mutant.SpanStart < 0 || mutant.SpanLength < 1 || mutant.SpanStart > source.Length - mutant.SpanLength
                    || source.Substring(mutant.SpanStart, mutant.SpanLength) != mutant.Original
                    || candidates.Any(other => other.Id != mutant.Id && other.SpanStart < mutant.SpanStart + mutant.SpanLength
                        && mutant.SpanStart < other.SpanStart + other.SpanLength)) continue;
                var node = root.FindNode(new(mutant.SpanStart, mutant.SpanLength), getInnermostNodeForTie: true);
                if (node is not BinaryExpressionSyntax binary || binary.Span.Start != mutant.SpanStart || binary.Span.Length != mutant.SpanLength
                    || binary.OperatorToken.Text is not (">" or ">=" or "<" or "<=")
                    || model.GetConstantValue(binary, token).HasValue
                    || model.GetOperation(binary, token) is not IBinaryOperation operation || operation.Type?.SpecialType != SpecialType.System_Boolean
                    || operation.IsLifted || operation.OperatorMethod != null
                    || operation.DescendantsAndSelf().OfType<IConversionOperation>().Any(conversion => conversion.OperatorMethod != null)
                    || !Numeric(operation.LeftOperand.Type) || !Numeric(operation.RightOperand.Type)
                    || !Numeric(model.GetTypeInfo(binary.Left, token).Type) || !Numeric(model.GetTypeInfo(binary.Right, token).Type)
                    || binary.DescendantNodes().OfType<SingleVariableDesignationSyntax>().Any()
                    || binary.Ancestors().Any(a => a is AttributeSyntax or ConstantPatternSyntax or CaseSwitchLabelSyntax
                        || a is LambdaExpressionSyntax lambda && model.GetTypeInfo(lambda, token).ConvertedType is INamedTypeSymbol named
                            && named.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>")) continue;
                var replacement = SyntaxFactory.ParseExpression(mutant.Replacement, options: (CSharpParseOptions)tree.Options);
                // Accept precisely the corresponding boundary operator with the same two operands.
                var changedOperator = binary.OperatorToken.Text switch { ">" => ">=", ">=" => ">", "<" => "<=", _ => "<" };
                if (replacement is not BinaryExpressionSyntax changed || changed.ContainsDiagnostics || changed.OperatorToken.Text != changedOperator
                    || !SyntaxFactory.AreEquivalent(binary.Left, changed.Left) || !SyntaxFactory.AreEquivalent(binary.Right, changed.Right)) continue;
                var id = ids.Count + 1;
                ids.Add(mutant.Id, id);
                replacements[binary] = SyntaxFactory.ConditionalExpression(
                    SyntaxFactory.ParseExpression("global::" + runtimeNamespace + ".Runtime.IsActive(" + id + ")"),
                    SyntaxFactory.ParenthesizedExpression(changed.WithoutTrivia()),
                    SyntaxFactory.ParenthesizedExpression(binary.WithoutTrivia())).WithTriviaFrom(binary);
            }
            if (replacements.Count > 0)
                sources[tree.FilePath] = root.ReplaceNodes(replacements.Keys, (node, _) => replacements[node]).ToFullString();
        }
        var literal = SyntaxFactory.Literal(environmentName).Text;
        var runtime = $$"""
            namespace {{runtimeNamespace}} {
                internal static class Runtime {
                    private static readonly int active = Read();
                    private static int Read() {
                        int value;
                        return global::System.Int32.TryParse(global::System.Environment.GetEnvironmentVariable({{literal}}), out value) ? value : 0;
                    }
                    internal static bool IsActive(int value) { return active == value; }
                }
            }
            """;
        return new(sources, ids, runtime);
    }
    private static bool Numeric(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double;
}
