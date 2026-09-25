// ScopeClassIdentity — gives each emitted method-scope class the IsTest flag and the method id
// that BC's inline-scope emit (the service tier's default) passes to ALMethodScope and its
// scope-class emit (what the runner compiles with) leaves out. BC's code-coverage recorder keys
// the per-test tables 2000000288/2000000289 on both (#4666, corpus codeunit 60925); the
// derivation is in docs/limitations.md#code-coverage-virtual-tables.
//
// TRAP: only add here. The one rename (an emitted GetMethodScopeFlags override, moved under a
// private name) leaves an override of the same signature in its place; never drop or re-sign a
// member an emitted type declares (precompiled-dll-respect.md, "Our AL output").
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AlRunner.Rewriters;

public static class ScopeClassIdentity
{
    internal const string MethodIdProviderType = "global::Microsoft.Dynamics.Nav.Runtime.IMethodIdProvider";
    internal const string FlagsType = "global::Microsoft.Dynamics.Nav.Runtime.MethodScopeFlags";
    internal const string EmittedFlagsMethodName = "αEmittedMethodScopeFlags";

    // BC's GetMethodScopeName: `<name>_Scope_<id>`, or `<name>_Scope__<abs(id)>` when id < 0.
    private static readonly Regex ScopeClassName = new(@"_Scope_(?<neg>_?)(?<abs>\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// The method id BC encoded in a scope class's name, or null for a name that carries none
    /// (a trigger's <c>X_Scope</c>). Throws for a name that has the shape but not an
    /// <see cref="int"/> — BC's encoding would then have changed.
    /// </summary>
    internal static int? MethodIdFromScopeClassName(string className)
    {
        var m = ScopeClassName.Match(className);
        if (!m.Success) return null;
        var abs = long.Parse(m.Groups["abs"].Value, CultureInfo.InvariantCulture);
        var id = m.Groups["neg"].Length > 0 ? -abs : abs;
        if (id < int.MinValue || id > int.MaxValue)
            throw new InvalidOperationException(
                $"ScopeClassIdentity: scope class '{className}' encodes method id {id}, outside Int32. "
                + "BC's scope-class naming (MethodCodeGenerator.GetMethodScopeName) has changed.");
        return (int)id;
    }

    /// <summary>Applies the pass to one generated source tree. Returns the same tree when it
    /// holds no scope class.</summary>
    public static SyntaxTree Apply(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var rewritten = new Rewriter().Visit(root);
        return ReferenceEquals(rewritten, root) ? tree : tree.WithRootAndOptions(rewritten, tree.Options);
    }

    private sealed class Rewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            // Test-method scope classes are the ones a [NavTest] method of THIS class constructs.
            var testScopeClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in node.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!HasAttribute(method.AttributeLists, "NavTest")) continue;
                foreach (var creation in method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                    if (creation.Type is IdentifierNameSyntax id)
                        testScopeClasses.Add(id.Identifier.ValueText);
            }

            var replacements = new Dictionary<ClassDeclarationSyntax, ClassDeclarationSyntax>();
            foreach (var original in node.Members.OfType<ClassDeclarationSyntax>())
            {
                var nested = (ClassDeclarationSyntax)VisitClassDeclaration(original)!;
                var withIdentity = AddIdentity(nested, testScopeClasses.Contains(original.Identifier.ValueText));
                if (!ReferenceEquals(withIdentity, original))
                    replacements[original] = withIdentity;
            }
            return replacements.Count == 0
                ? node
                : node.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
        }

        private static ClassDeclarationSyntax AddIdentity(ClassDeclarationSyntax cls, bool isTestScope)
        {
            if (!DerivesFromNavMethodScope(cls)) return cls;
            var methodId = MethodIdFromScopeClassName(cls.Identifier.ValueText);
            if (methodId == null) return cls;

            var members = new List<MemberDeclarationSyntax>();
            members.Add(Member(
                $"int {MethodIdProviderType}.MethodId => {methodId.Value.ToString(CultureInfo.InvariantCulture)};"));

            if (isTestScope)
            {
                var emitted = cls.Members.OfType<MethodDeclarationSyntax>()
                    .SingleOrDefault(m => m.Identifier.ValueText == "GetMethodScopeFlags"
                        && m.ParameterList.Parameters.Count == 0);
                if (emitted != null)
                {
                    // Keep BC's own body, including its base call, under a private name.
                    var renamed = emitted
                        .WithIdentifier(SyntaxFactory.Identifier(EmittedFlagsMethodName))
                        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));
                    cls = cls.ReplaceNode(emitted, renamed);
                    members.Add(Member(
                        $"protected override {FlagsType} GetMethodScopeFlags() => {EmittedFlagsMethodName}() | {FlagsType}.IsTest;"));
                }
                else
                {
                    members.Add(Member(
                        $"protected override {FlagsType} GetMethodScopeFlags() => base.GetMethodScopeFlags() | {FlagsType}.IsTest;"));
                }
            }

            var baseList = cls.BaseList!.AddTypes(
                SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(MethodIdProviderType)));
            return cls.WithBaseList(baseList).AddMembers(members.ToArray());
        }

        private static MemberDeclarationSyntax Member(string code)
            => SyntaxFactory.ParseMemberDeclaration(code)
               ?? throw new InvalidOperationException($"ScopeClassIdentity: could not parse '{code}'");

        private static bool DerivesFromNavMethodScope(ClassDeclarationSyntax cls)
        {
            var first = cls.BaseList?.Types.FirstOrDefault()?.Type;
            var generic = first switch
            {
                GenericNameSyntax g => g,
                QualifiedNameSyntax { Right: GenericNameSyntax g } => g,
                AliasQualifiedNameSyntax { Name: GenericNameSyntax g } => g,
                _ => null,
            };
            return generic?.Identifier.ValueText == "NavMethodScope";
        }

        private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name)
        {
            foreach (var list in lists)
                foreach (var attr in list.Attributes)
                {
                    var n = attr.Name switch
                    {
                        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
                        SimpleNameSyntax s => s.Identifier.ValueText,
                        _ => "",
                    };
                    if (n == name || n == name + "Attribute") return true;
                }
            return false;
        }
    }
}
