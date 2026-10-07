using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Walker.Core;

namespace Walker.Roslyn;

public sealed record InstrumentationResult(IReadOnlyDictionary<string, string> Sources,
    IReadOnlyDictionary<string, int> ActiveIds, string RuntimeSource)
{
    public IReadOnlySet<int> BatchableIds { get; init; } = new HashSet<int>();
}

public sealed class MutationInstrumenter
{
    public InstrumentationResult Instrument(CSharpCompilation compilation, IReadOnlyList<Mutant> selected,
        string runtimeNamespace, string environmentName, CancellationToken token = default)
    {
        if (!SyntaxFacts.IsValidIdentifier(runtimeNamespace)) throw new ArgumentException("Invalid generated namespace.", nameof(runtimeNamespace));
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var batchable = new HashSet<int>();
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
                // Start narrowly: a static expression-bodied comparison of numeric
                // parameters/literals cannot itself write state or invoke user code.
                if (binary.Parent is ArrowExpressionClauseSyntax { Parent: MethodDeclarationSyntax method }
                    && method.Modifiers.Any(SyntaxKind.StaticKeyword)
                    && method.Parent is ClassDeclarationSyntax type && type.BaseList == null
                    && !type.Members.Any(m => m is FieldDeclarationSyntax or PropertyDeclarationSyntax or ConstructorDeclarationSyntax
                        or EventDeclarationSyntax or EventFieldDeclarationSyntax)
                    && model.GetDeclaredSymbol(type, token) is { } owner
                    && owner.GetMembers(method.Identifier.ValueText).Length == 1
                    && !owner.GetMembers().Any(m => m is IFieldSymbol or IPropertySymbol or IEventSymbol
                        || m is IMethodSymbol { MethodKind: MethodKind.StaticConstructor }
                        || m is IMethodSymbol { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: false })
                    && binary.Left is IdentifierNameSyntax && binary.Right is LiteralExpressionSyntax
                    && model.GetSymbolInfo(binary.Left, token).Symbol is IParameterSymbol)
                    batchable.Add(id);
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
                    private static readonly global::System.Collections.Generic.HashSet<int> active = Read();
                    private static readonly bool collect = global::System.Environment.GetEnvironmentVariable("WALKER_COVERAGE_ENV") == {{literal}};
                    private static global::System.Collections.Generic.HashSet<int> Read() {
                        var result = new global::System.Collections.Generic.HashSet<int>();
                        var text = global::System.Environment.GetEnvironmentVariable({{literal}}) ?? "";
                        foreach (var item in text.Split(',')) { int value; if (global::System.Int32.TryParse(item, out value)) result.Add(value); }
                        return result;
                    }
                    internal static bool IsActive(int value) {
                        if (collect) {
                            var hit = global::System.AppDomain.CurrentDomain.GetData({{literal}} + "_coverage") as global::System.Action<int>;
                            if (hit != null) hit(value);
                        }
                        return active.Contains(value);
                    }
                }
            }
            """;
        return new(sources, ids, runtime) { BatchableIds = batchable };
    }
    private static bool Numeric(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double;
}
