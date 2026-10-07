using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Walker.Roslyn;

// Coverage alone does not prove independence: one test can store a mutated
// value for another test. Accept only a small, inspectable assertion-only test
// shape. Anything outside this grammar retains isolated single-mutant runs.
public static class TestBatchSafety
{
    public static bool Allows(IEnumerable<string> sources, IReadOnlySet<string> pureMethods)
    {
        foreach (var source in sources)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            // This syntactic proof does not have the evaluated test compiler's
            // conditional symbols. Refuse directives rather than inspecting the
            // wrong branch and overlooking shared state or unknown calls.
            if (root.ContainsDiagnostics || root.DescendantTrivia(descendIntoTrivia: true).Any(t => t.GetStructure() is DirectiveTriviaSyntax)
                || root.DescendantNodes().Any(n => n is
                FieldDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax or EventFieldDeclarationSyntax
                or ConstructorDeclarationSyntax or DestructorDeclarationSyntax or BaseListSyntax
                or AssignmentExpressionSyntax or AwaitExpressionSyntax or AnonymousFunctionExpressionSyntax
                or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                or LockStatementSyntax or UnsafeStatementSyntax
                || n is CastExpressionSyntax cast && cast.Type is not PredefinedTypeSyntax
                || n is DefaultExpressionSyntax value && value.Type is not PredefinedTypeSyntax
                || n is TypeOfExpressionSyntax
                || n is PrefixUnaryExpressionSyntax pre && pre.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
                || n is PostfixUnaryExpressionSyntax post && post.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression)) return false;
            if (root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(u => u.Alias != null || !u.StaticKeyword.IsKind(SyntaxKind.None))) return false;
            if (root.DescendantNodes().OfType<TypeDeclarationSyntax>().Any(t => t.Identifier.ValueText == "Assert")) return false;
            if (root.DescendantNodes().OfType<ParameterSyntax>().Any(p => p.Type is not PredefinedTypeSyntax)) return false;
            foreach (var variable in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
            {
                if (variable.Type is PredefinedTypeSyntax || variable.Type.ToString() == "var") continue;
                if (variable.Type is ArrayTypeSyntax { ElementType: GenericNameSyntax generic }
                    && generic.Identifier.ValueText == "Func" && generic.TypeArgumentList.Arguments.All(a => a is PredefinedTypeSyntax)) continue;
                return false;
            }
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (access.Parent is MemberAccessExpressionSyntax) continue;
                var name = access.ToString();
                if (!pureMethods.Contains(name) && name is not ("Assert.True" or "Assert.False" or "Assert.Equal"
                    or "Xunit.Assert.True" or "Xunit.Assert.False" or "Xunit.Assert.Equal")) return false;
            }
            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                if (attribute.Parent?.Parent is CompilationUnitSyntax) continue; // generated assembly attributes
                if (attribute.Name.ToString() is not ("Fact" or "Theory" or "InlineData" or "Xunit.Fact" or "Xunit.Theory" or "Xunit.InlineData")) return false;
            }
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression.ToString();
                if (name is "Assert.True" or "Assert.False" or "Assert.Equal" or "Xunit.Assert.True" or "Xunit.Assert.False" or "Xunit.Assert.Equal") continue;
                if (pureMethods.Contains(name)) continue;
                if (invocation.Expression is ElementAccessExpressionSyntax { Expression: IdentifierNameSyntax array })
                {
                    var declarations = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                        .Where(v => v.Identifier.ValueText == array.Identifier.ValueText).ToArray();
                    var declaration = declarations.Length == 1 ? declarations[0] : null;
                    if (declaration?.Initializer?.Value is CollectionExpressionSyntax collection
                        && collection.Elements.Count > 0 && collection.Elements.All(e => e is ExpressionElementSyntax item && pureMethods.Contains(item.Expression.ToString()))) continue;
                }
                return false;
            }
            // No non-test helpers, operators or custom conversions can hide writes.
            if (root.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.AttributeLists.Count == 0)
                || root.DescendantNodes().Any(n => n is OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax)) return false;
        }
        return true;
    }
}
