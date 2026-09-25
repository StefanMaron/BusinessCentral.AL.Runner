// ScopeClassIdentity — gives each emitted method-scope CLASS the two facts BC's inline-scope
// emit passes to ALMethodScope, and which BC's scope-class emit leaves out (#4666).
//
// The runner compiles with NavCA.EmitOptions.Default, which emits one nested
// `X_Scope_<id> : NavMethodScope<T>` class per AL method. A service tier compiles with
// EnableInlinedMethodCodeGeneration (default true on 27.0 and 28.4), which emits
// `new ALMethodScope(this, name, flags, Method.Id)` instead. Only the inline form carries:
//   - MethodScopeFlags.IsTest for a [Test] method (MethodCodeGenerator.GetMethodScopeFlags);
//     the scope-class form's GetMethodScopeFlags override only ever adds
//     IsOnPremiseCallerRequired / IsDebugDisallowed.
//   - the AL method id, through IMethodIdProvider.MethodId (ALMethodScope implements it).
// BC's code-coverage recorder reads both: SingleSessionCodeCoverageRecorder.ProcessStart
// opens a per-test record only for a scope whose IsTest is set, and every per-test row and
// every table-2000000288 row is keyed by IMethodIdProvider.MethodId (-1, the "no id" answer,
// is skipped). So without them tables 2000000288 and 2000000289 read empty.
//
// This pass adds, to each scope class whose name carries a method id:
//   - `IMethodIdProvider` with that id (the name is BC's own encoding of Method.Id:
//     `_Scope_<id>` / `_Scope__<abs id>` for a negative one), and
//   - for the scope class a [NavTest] method constructs, a GetMethodScopeFlags override that
//     ORs IsTest into whatever the emitted class already answered.
// It renames nothing and changes no existing signature, so the emitted assembly's surface
// only grows (precompiled-dll-respect.md, "Our AL output"). It runs inside the compile
// pipeline, before the DLL is finalised, so a cached DLL carries it.
//
// Not covered: a trigger's scope class is named `X_Scope` with no id, so its MethodId stays
// the scope-class default (none). See docs/limitations.md#code-coverage-virtual-tables.
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

            var updated = node.ReplaceNodes(
                node.Members.OfType<ClassDeclarationSyntax>(),
                (original, _) => (SyntaxNode?)Visit(original) is ClassDeclarationSyntax nested
                    ? AddIdentity(nested, testScopeClasses.Contains(original.Identifier.ValueText))
                    : original);
            return updated;
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
