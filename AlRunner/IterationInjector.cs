using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AlRunner;

/// <summary>
/// Second-pass Roslyn rewriter that injects IterationTracker calls around loops.
/// Mirrors the ValueCaptureInjector pattern — runs unconditionally since
/// IterationTracker no-ops when disabled.
/// </summary>
public sealed class IterationInjector : CSharpSyntaxRewriter
{
    private string? _currentScopeClass;
    private int _nextLoopIdHint;

    public static SyntaxNode Inject(SyntaxNode root)
    {
        var injector = new IterationInjector();
        return injector.Visit(root);
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var previous = _currentScopeClass;
        if (node.Identifier.Text.Contains("_Scope"))
            _currentScopeClass = node.Identifier.Text;
        var result = base.VisitClassDeclaration(node);
        _currentScopeClass = previous;
        return result;
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        if (_currentScopeClass is null) return base.VisitForStatement(node);
        // Visit children first so nested loops are processed inside-out
        var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
        return WrapLoop(visited, visited.Statement);
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        if (_currentScopeClass is null) return base.VisitWhileStatement(node);
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return WrapLoop(visited, visited.Statement);
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        if (_currentScopeClass is null) return base.VisitDoStatement(node);
        var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return WrapLoop(visited, visited.Statement);
    }

    private SyntaxNode WrapLoop(StatementSyntax loopNode, StatementSyntax body)
    {
        var loopIdVar = $"__alr_loopId_{_nextLoopIdHint++}";
        var (startLine, endLine) = ExtractStmtHitRange(loopNode);

        // Ensure body is a block
        var bodyBlock = body is BlockSyntax block ? block : SyntaxFactory.Block(body);

        // Only EnterIteration is injected at the top of the body.
        // No EndIteration needed — EnterIteration finalizes the previous
        // iteration, and ExitLoop (in the finally block) finalizes the last one.
        // This is robust against break/continue/early-exit in any loop structure.
        var enterIter = SyntaxFactory.ParseStatement(
            $"AlRunner.Runtime.IterationTracker.EnterIteration({loopIdVar});\n");

        var newStatements = new List<StatementSyntax> { enterIter };
        newStatements.AddRange(bodyBlock.Statements);
        var newBody = SyntaxFactory.Block(newStatements);

        // Replace loop body with instrumented body
        var instrumentedLoop = ReplaceBody(loopNode, newBody);

        // Build: var __alr_loopId_N = AlRunner.Runtime.IterationTracker.EnterLoop(scopeName, startLine, endLine);
        var enterLoop = SyntaxFactory.ParseStatement(
            $"var {loopIdVar} = AlRunner.Runtime.IterationTracker.EnterLoop(\"{_currentScopeClass}\", {startLine}, {endLine});\n");

        // Build: AlRunner.Runtime.IterationTracker.ExitLoop(__alr_loopId_N);
        var exitLoop = SyntaxFactory.ParseStatement(
            $"AlRunner.Runtime.IterationTracker.ExitLoop({loopIdVar});\n");

        // Wrap in try/finally to ensure ExitLoop always runs
        var tryFinally = SyntaxFactory.TryStatement(
            SyntaxFactory.Block(SyntaxFactory.SingletonList(instrumentedLoop)),
            SyntaxFactory.List<CatchClauseSyntax>(),
            SyntaxFactory.FinallyClause(SyntaxFactory.Block(exitLoop)));

        return SyntaxFactory.Block(enterLoop, tryFinally);
    }

    private static StatementSyntax ReplaceBody(StatementSyntax loopNode, BlockSyntax newBody)
    {
        return loopNode switch
        {
            ForStatementSyntax f => f.WithStatement(newBody),
            WhileStatementSyntax w => w.WithStatement(newBody),
            DoStatementSyntax d => d.WithStatement(newBody),
            _ => throw new InvalidOperationException($"Unexpected loop type: {loopNode.GetType()}")
        };
    }

    /// <summary>
    /// Extract the min and max StmtHit/CStmtHit IDs from descendant nodes
    /// as a proxy for AL source line range.
    /// </summary>
    private static (int startLine, int endLine) ExtractStmtHitRange(SyntaxNode node)
    {
        var ids = new List<int>();
        foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is IdentifierNameSyntax id &&
                (id.Identifier.Text == "StmtHit" || id.Identifier.Text == "CStmtHit") &&
                invocation.ArgumentList.Arguments.Count == 1 &&
                invocation.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal &&
                literal.Token.Value is int stmtId)
            {
                ids.Add(stmtId);
            }
        }
        if (ids.Count == 0) return (0, 0);
        return (ids.Min(), ids.Max());
    }
}
