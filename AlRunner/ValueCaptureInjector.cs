using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AlRunner;

/// <summary>
/// Second-pass rewriter that injects <c>AlRunner.Runtime.ValueCapture.Capture(...)</c>
/// calls after each assignment to a scope field inside a generated scope
/// class. The rewriter runs unconditionally — when capture mode is disabled,
/// <c>ValueCapture.Capture</c> is a cheap no-op that returns immediately.
///
/// The injected calls are keyed by the neighboring <c>StmtHit(N)</c> ID so
/// downstream tooling can correlate each capture back to an AL source
/// line via the existing SourceLineMapper.
/// </summary>
public sealed class ValueCaptureInjector : CSharpSyntaxRewriter
{
    private string? _currentScopeClass;
    private readonly string _objectName;
    private HashSet<string> _runtimeTypeFields = new();

    private ValueCaptureInjector(string objectName) { _objectName = objectName; }

    public static SyntaxNode Inject(SyntaxNode root, string objectName = "")
    {
        var injector = new ValueCaptureInjector(objectName);
        return injector.Visit(root);
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var previous = _currentScopeClass;
        var previousFields = _runtimeTypeFields;
        // Only instrument scope classes (they carry the user-facing locals);
        // the outer codeunit/record wrappers hold only plumbing.
        if (node.Identifier.Text.Contains("_Scope"))
        {
            _currentScopeClass = node.Identifier.Text;
            _runtimeTypeFields = CollectRuntimeTypeFields(node);
        }
        var result = base.VisitClassDeclaration(node);
        _currentScopeClass = previous;
        _runtimeTypeFields = previousFields;
        return result;
    }

    /// <summary>
    /// Collect field names whose declared type is an internal runtime type
    /// (MockCodeunitHandle, MockRecordHandle, etc.). These hold AL object
    /// references whose .ToString() returns implementation details, not
    /// user-meaningful values.
    /// </summary>
    private static HashSet<string> CollectRuntimeTypeFields(ClassDeclarationSyntax classNode)
    {
        var fields = new HashSet<string>();
        foreach (var member in classNode.Members.OfType<FieldDeclarationSyntax>())
        {
            var typeName = member.Declaration.Type.ToString();
            if (typeName.StartsWith("Mock", StringComparison.Ordinal) ||
                typeName.StartsWith("Nav", StringComparison.Ordinal) ||
                typeName == "ITreeObject")
            {
                foreach (var variable in member.Declaration.Variables)
                    fields.Add(variable.Identifier.ValueText);
            }
        }
        return fields;
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        if (_currentScopeClass is null)
            return base.VisitBlock(node);

        var visited = (BlockSyntax)base.VisitBlock(node)!;

        var newStatements = new List<StatementSyntax>(visited.Statements.Count);
        int currentStmtId = -1;
        foreach (var stmt in visited.Statements)
        {
            newStatements.Add(stmt);

            // Look for StmtHit(N) / CStmtHit(N) calls to latch the current statement ID.
            var stmtHit = TryExtractStmtHitId(stmt);
            if (stmtHit.HasValue)
                currentStmtId = stmtHit.Value;

            if (currentStmtId < 0) continue;

            // Now check if the statement contains an assignment to a scope field.
            // `this.field = ...`  OR  `this.field += ...` etc.
            var fieldName = TryExtractAssignedFieldName(stmt);
            if (fieldName is null) continue;

            // Skip BC plumbing fields and internal runtime type fields
            if (IsPlumbingField(fieldName)) continue;
            if (_runtimeTypeFields.Contains(fieldName)) continue;

            newStatements.Add(BuildCaptureCall(fieldName, currentStmtId));
        }

        return visited.WithStatements(SyntaxFactory.List(newStatements));
    }

    private static int? TryExtractStmtHitId(StatementSyntax stmt)
    {
        // Pattern: `StmtHit(N);` or `CStmtHit(N);` as an expression statement.
        if (stmt is ExpressionStatementSyntax expr &&
            expr.Expression is InvocationExpressionSyntax inv &&
            inv.Expression is IdentifierNameSyntax id &&
            (id.Identifier.Text == "StmtHit" || id.Identifier.Text == "CStmtHit") &&
            inv.ArgumentList.Arguments.Count == 1 &&
            inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
            int.TryParse(lit.Token.ValueText, out var id1))
        {
            return id1;
        }
        // Pattern: `if (CStmtHit(N) & ...)` — extract from condition.
        if (stmt is IfStatementSyntax ifs)
        {
            foreach (var condInv in ifs.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (condInv.Expression is IdentifierNameSyntax idn &&
                    (idn.Identifier.Text == "StmtHit" || idn.Identifier.Text == "CStmtHit") &&
                    condInv.ArgumentList.Arguments.Count == 1 &&
                    condInv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax l &&
                    int.TryParse(l.Token.ValueText, out var id2))
                    return id2;
            }
        }
        return null;
    }

    private static string? TryExtractAssignedFieldName(StatementSyntax stmt)
    {
        if (stmt is not ExpressionStatementSyntax expr) return null;
        if (expr.Expression is not AssignmentExpressionSyntax asgn) return null;
        // Left side: this.fieldName
        if (asgn.Left is not MemberAccessExpressionSyntax ma) return null;
        if (ma.Expression is not ThisExpressionSyntax) return null;
        // ValueText returns the decoded identifier (e.g. "γretVal" not
        // "\u03b3retVal"), which IsPlumbingField needs to inspect real
        // unicode chars rather than escape sequences.
        return ma.Name.Identifier.ValueText;
    }

    private static bool IsPlumbingField(string name)
    {
        if (name.Length == 0) return true;
        // BC emits β-prefixed fields for scope plumbing and γ-prefixed for
        // return values; skip both to avoid noise.
        var c = name[0];
        if (c == 'β' || c == 'γ' || c == '\u03b2' || c == '\u03b3') return true;
        if (name.StartsWith("__", StringComparison.Ordinal)) return true;
        if (name == "_parent" || name == "me") return true;
        return false;
    }

    private StatementSyntax BuildCaptureCall(string fieldName, int stmtId)
    {
        // AlRunner.Runtime.ValueCapture.Capture(
        //   <scopeClass>, <objectName>, <fieldName>, (object?)this.<fieldName>, <stmtId>);
        var scopeLit = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(_currentScopeClass!));
        var objLit = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(_objectName));
        var nameLit = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(fieldName));
        var fieldAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.IdentifierName(fieldName));
        var valueArg = SyntaxFactory.CastExpression(
            SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))),
            fieldAccess);
        var idLit = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(stmtId));

        var target = SyntaxFactory.ParseExpression("AlRunner.Runtime.ValueCapture.Capture");

        var call = SyntaxFactory.InvocationExpression(
            target,
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Argument(scopeLit),
                SyntaxFactory.Argument(objLit),
                SyntaxFactory.Argument(nameLit),
                SyntaxFactory.Argument(valueArg),
                SyntaxFactory.Argument(idLit)
            })));

        return SyntaxFactory.ExpressionStatement(call);
    }
}
