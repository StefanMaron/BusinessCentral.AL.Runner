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

        // Plan E5 Group B (G2 fix): extract the loop variable name so we can
        // inject a per-iteration capture call. The AL→C# rewriter emits
        // `for i := <expr> to <expr> do` as:
        //   this.i = 1;              ← initialisation before the loop
        //   for (; this.i <= @tmp0;) ← no inline Declaration; variable lives on `this`
        //
        // So node.Declaration is null; instead we read the left-hand side of
        // the condition expression: `this.<name> <= @tmp<N>` → name == "i".
        // If the condition doesn't follow that pattern (while/do or some other
        // for form), loopVarName stays null and no capture is injected.
        string? loopVarName = null;
        if (visited.Declaration is { Variables: { Count: > 0 } } decl)
        {
            // Inline-declaration form: `for (int i = ...; ...)` — keep for
            // forward-compatibility even though the current rewriter never emits this.
            loopVarName = decl.Variables[0].Identifier.Text;
        }
        else if (visited.Condition is BinaryExpressionSyntax bin &&
                 bin.Left is MemberAccessExpressionSyntax ma &&
                 ma.Expression is ThisExpressionSyntax)
        {
            // Field-on-this form: `for (; this.i <= @tmp0;)` — the common case
            // emitted by the BC AL→C# transpiler for AL `for i := ... to ... do`.
            var name = ma.Name.Identifier.ValueText;
            if (!IsPlumbingField(name))
                loopVarName = name;
        }

        return WrapLoop(visited, visited.Statement, loopVarName);
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        if (_currentScopeClass is null) return base.VisitWhileStatement(node);
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        // While loops don't have an inline-declared loop variable; pass null.
        return WrapLoop(visited, visited.Statement, loopVarName: null);
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        if (_currentScopeClass is null) return base.VisitDoStatement(node);
        var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
        // Do-while loops don't have an inline-declared loop variable; pass null.
        return WrapLoop(visited, visited.Statement, loopVarName: null);
    }

    /// <summary>
    /// Returns true for BC plumbing fields (β/γ-prefixed and double-underscore-prefixed)
    /// that should not be captured. Mirrors the same logic in ValueCaptureInjector.
    /// </summary>
    private static bool IsPlumbingField(string name)
    {
        if (name.Length == 0) return true;
        var c = name[0];
        // BC emits β-prefixed fields for scope plumbing and γ-prefixed for
        // return values; skip both to avoid noise.
        if (c == 'β' || c == 'γ') return true;
        if (name.StartsWith("__", StringComparison.Ordinal)) return true;
        if (name == "_parent" || name == "me") return true;
        return false;
    }

    private SyntaxNode WrapLoop(StatementSyntax loopNode, StatementSyntax body, string? loopVarName)
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

        // Plan E5 Group B (G2 fix): inject a per-iteration capture for the
        // loop variable so it appears in step.capturedValues alongside
        // assignment targets. statementId 0 anchors the capture at the
        // for-statement's start. If loopVarName is null (while/do, or a
        // for whose condition doesn't follow the `this.<name> <= ...`
        // pattern), skip the injection — there's no loop variable to capture.
        if (loopVarName != null)
        {
            var captureLoopVar = SyntaxFactory.ParseStatement(
                $"AlRunner.Runtime.ValueCapture.Capture(\"{_currentScopeClass}\", \"{_currentScopeClass}\", \"{loopVarName}\", (object?)this.{loopVarName}, 0);\n");
            newStatements.Add(captureLoopVar);
        }

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
