using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AlRunner;

/// <summary>
/// Post-rewrite Roslyn <see cref="CSharpSyntaxRewriter"/> that prepends
/// <c>#line N "src/Foo.al"</c> leading trivia before each statement that
/// carries a <c>StmtHit(N)</c> or <c>CStmtHit(N)</c> coverage anchor.
///
/// This makes Roslyn emit portable-PDB sequence points that reference the
/// original .al file, so <see cref="System.Diagnostics.StackFrame.GetFileName"/>
/// and <see cref="System.Diagnostics.StackFrame.GetFileLineNumber"/> return
/// .al paths natively in exception stack traces.
///
/// Call <see cref="Inject"/> after all other rewriter passes and before
/// <c>tree.ToFullString()</c>.  Only activated when
/// <see cref="PipelineOptions.EmitLineDirectives"/> is true.
/// </summary>
public sealed class LineDirectiveInjector : CSharpSyntaxRewriter
{
    // (scopeClassName, stmtIndex) → (alLine, alColumn)
    private readonly IReadOnlyDictionary<(string Scope, int StmtIndex), (int Line, int Column)> _sourceSpans;

    // scopeClassName → AL object name (from CoverageReport.BuildScopeToObjectMap)
    private readonly IReadOnlyDictionary<string, string> _scopeToObject;

    // AL object name → relative file path (SourceFileMapper.GetFile)
    private readonly Func<string, string?> _getFile;

    // Current scope class name on the traversal stack
    private readonly Stack<string?> _scopeStack = new();
    private string? CurrentScope => _scopeStack.Count > 0 ? _scopeStack.Peek() : null;

    private LineDirectiveInjector(
        IReadOnlyDictionary<(string Scope, int StmtIndex), (int Line, int Column)> sourceSpans,
        IReadOnlyDictionary<string, string> scopeToObject,
        Func<string, string?> getFile)
    {
        _sourceSpans = sourceSpans;
        _scopeToObject = scopeToObject;
        _getFile = getFile;
    }

    /// <summary>
    /// Run the injector over <paramref name="root"/> and return the rewritten node.
    /// </summary>
    public static SyntaxNode Inject(
        SyntaxNode root,
        IReadOnlyDictionary<(string Scope, int StmtIndex), (int Line, int Column)> sourceSpans,
        IReadOnlyDictionary<string, string> scopeToObject,
        Func<string, string?> getFile)
    {
        var injector = new LineDirectiveInjector(sourceSpans, scopeToObject, getFile);
        return injector.Visit(root)!;
    }

    // -----------------------------------------------------------------------
    // Scope tracking: push/pop the class name so visit methods know their
    // enclosing scope.  Only scope classes (_Scope_HASH pattern) are pushed
    // since those are the ones with SourceSpans attributes.
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        // Push scope class name (or null for outer codeunit/record classes)
        var className = node.Identifier.Text;
        bool isScopeClass = className.Contains("_Scope");
        _scopeStack.Push(isScopeClass ? className : null);
        var result = base.VisitClassDeclaration(node);
        _scopeStack.Pop();
        return result;
    }

    // -----------------------------------------------------------------------
    // ExpressionStatement: inject #line before StmtHit(N) statements.
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var visited = (ExpressionStatementSyntax)base.VisitExpressionStatement(node)!;
        if (CurrentScope is null) return visited;

        var stmtIndex = TryExtractStmtHitIndex(visited);
        if (stmtIndex is null) return visited;

        var directive = BuildDirectiveTrivia(CurrentScope, stmtIndex.Value);
        if (directive is null) return visited;

        // Re-entry guard: skip if a #line directive is already present.
        if (visited.GetLeadingTrivia().Any(t => t.IsDirective && t.GetStructure() is LineDirectiveTriviaSyntax))
            return visited;

        return visited.WithLeadingTrivia(
            directive.Value.AddRange(visited.GetLeadingTrivia()));
    }

    // -----------------------------------------------------------------------
    // IfStatement: inject #line before `if (CStmtHit(N) & ...)` statements.
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
        if (CurrentScope is null) return visited;

        var stmtIndex = TryExtractCStmtHitFromCondition(visited.Condition);
        if (stmtIndex is null) return visited;

        var directive = BuildDirectiveTrivia(CurrentScope, stmtIndex.Value);
        if (directive is null) return visited;

        // Re-entry guard: skip if a #line directive is already present.
        if (visited.GetLeadingTrivia().Any(t => t.IsDirective && t.GetStructure() is LineDirectiveTriviaSyntax))
            return visited;

        return visited.WithLeadingTrivia(
            directive.Value.AddRange(visited.GetLeadingTrivia()));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// If <paramref name="node"/> is an expression-statement that is a bare
    /// <c>StmtHit(N)</c> call, return N; otherwise null.
    /// </summary>
    private static int? TryExtractStmtHitIndex(ExpressionStatementSyntax node)
    {
        if (node.Expression is not InvocationExpressionSyntax inv) return null;

        // The identifier might be a simple name: StmtHit, or a member access like
        // SomeClass.StmtHit — walk to the rightmost name token.
        var nameText = GetInnermostName(inv.Expression);
        if (nameText != "StmtHit" && nameText != "CStmtHit") return null;

        if (inv.ArgumentList.Arguments.Count != 1) return null;
        if (inv.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax lit) return null;
        if (!int.TryParse(lit.Token.ValueText, out var idx)) return null;
        return idx;
    }

    /// <summary>
    /// Walk the condition expression and extract the index from the first
    /// <c>CStmtHit(N)</c> (or <c>StmtHit(N)</c>) invocation found.
    /// </summary>
    private static int? TryExtractCStmtHitFromCondition(ExpressionSyntax condition)
    {
        foreach (var inv in condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var nameText = GetInnermostName(inv.Expression);
            if ((nameText == "CStmtHit" || nameText == "StmtHit") &&
                inv.ArgumentList.Arguments.Count == 1 &&
                inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
                int.TryParse(lit.Token.ValueText, out var idx))
            {
                return idx;
            }
        }
        return null;
    }

    /// <summary>
    /// Return the rightmost identifier text for an expression like
    /// <c>Foo.Bar.Baz</c> (returns "Baz") or <c>Baz</c> (returns "Baz").
    /// </summary>
    private static string? GetInnermostName(ExpressionSyntax expr)
    {
        return expr switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => GetInnermostName(ma.Name),
            GenericNameSyntax gn => gn.Identifier.Text,
            _ => null
        };
    }

    /// <summary>
    /// Build the <c>#line N "path/to/Foo.al"</c> directive trivia for the given
    /// (scope, stmtIndex) pair, or null if resolution fails.
    /// </summary>
    private SyntaxTriviaList? BuildDirectiveTrivia(string scopeClass, int stmtIndex)
    {
        if (!_sourceSpans.TryGetValue((scopeClass, stmtIndex), out var pos)) return null;
        if (!_scopeToObject.TryGetValue(scopeClass, out var objectName)) return null;
        var filePath = _getFile(objectName);
        if (filePath is null) return null;

        // Normalize to forward slashes (SourceFileMapper already does this, but be
        // defensive in case callers pass a raw Windows path).
        var normalizedPath = filePath.Replace('\\', '/');

        // Use ParseLeadingTrivia: reliable across Roslyn versions and avoids
        // having to manually assemble the optional token set for LineDirectiveTrivia.
        var directiveText = $"#line {pos.Line} \"{normalizedPath}\"\n";
        return SyntaxFactory.ParseLeadingTrivia(directiveText);
    }
}
