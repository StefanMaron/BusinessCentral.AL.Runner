// AlMemberSyntaxIndex - the syntax facts the capture hooks need about every AL
// trigger/procedure in a bundle, read with BC's own syntax tree (issue #2056):
//   - its LOOPS (AlLoopSite: kind, loop variable, header range, body statements,
//     nesting) for iterationTracking - BC's [SourceSpans] carry file/line/column per
//     statement but nothing says "these statements form a loop body", and the StmtHit
//     stream alone cannot tell a nested loop's repeat from its parent's (AlLoopModel.cs);
//   - its statement WRITE SETS (AlStatementWrites) for full-fidelity captureValues -
//     which locals each statement assigns, so a same-value re-assignment is still a
//     record (AlWriteSetModel.cs).
//
// Parsed once per `execute` request that asks for captureValues or iterationTracking
// (HandleServerExecute), with the same preprocessor symbols BcCompiler.Emit uses for the
// bundle, so `#if` regions resolve the way they did when the code was compiled. Cost:
// one SyntaxTree.ParseObjectText per .al file, the same call the compiler already made -
// independent of the compile cache on purpose, since a cache HIT skips BcCompiler
// entirely and this index must still exist.
//
// Lookup is by (file, member name) - the same identity AlCoverageTracker resolves a
// scope class to (AlCoverageSourceMap for the file, [NavName] on the scope type for the
// member). Field/action triggers can share a name within one object (two fields'
// OnValidate), so FindMember disambiguates by which member body contains the scope's
// first statement.
using Microsoft.Dynamics.Nav.CodeAnalysis;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Infrastructure;

/// <summary>One AL trigger/procedure: the loops in its body (document order; nested
/// loops after their parent) and the write set of every assigning statement in it.
/// <c>Sites</c>/<c>Writes</c> are empty for a member without loops/assignments - still
/// listed, so a lookup can tell "found, nothing there" from "no such member".</summary>
public sealed record AlMemberSyntax(
    string FilePath,
    string Name,
    AlTextRange BodyRange,
    IReadOnlyList<AlLoopSite> Sites,
    IReadOnlyList<AlStatementWrites> Writes);

public sealed class AlMemberSyntaxIndex
{
    // Built-ins whose FIRST argument is by reference. Not a list of every by-ref BC
    // function - only the ones whose whole purpose is to assign that argument.
    private static readonly HashSet<string> ByRefFirstArgumentBuiltins =
        new(StringComparer.OrdinalIgnoreCase) { "Clear", "Evaluate" };

    private readonly Dictionary<string, List<AlMemberSyntax>> _byFile = new(StringComparer.OrdinalIgnoreCase);

    private AlMemberSyntaxIndex() { }

    /// <summary>Scans every *.al file under <paramref name="roots"/> (recursively, the
    /// same walk AlCoverageSourceMap.Build does) and indexes its members. Each root's own
    /// app.json supplies that bundle's preprocessor symbols, as in BcCompiler.</summary>
    public static AlMemberSyntaxIndex Build(IEnumerable<string> roots)
    {
        var members = new List<AlMemberSyntax>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var appJson = Path.Combine(root, "app.json");
            var symbols = PreprocessorSymbols(File.Exists(appJson) ? appJson : null);
            foreach (var file in SafeDirectoryScan.Files(root, "*.al"))
            {
                string source;
                try { source = File.ReadAllText(file); }
                catch (IOException) { continue; }
                members.AddRange(Parse(source, file, symbols));
            }
        }
        return FromMembers(members);
    }

    public static AlMemberSyntaxIndex FromMembers(IEnumerable<AlMemberSyntax> members)
    {
        var index = new AlMemberSyntaxIndex();
        foreach (var m in members)
        {
            if (!index._byFile.TryGetValue(m.FilePath, out var list))
                index._byFile[m.FilePath] = list = new List<AlMemberSyntax>();
            list.Add(m);
        }
        return index;
    }

    /// <summary>
    /// The member named <paramref name="memberName"/> in <paramref name="filePath"/>, or
    /// null when there is none. When several members share the name (field triggers),
    /// <paramref name="statementStart"/> - any statement position of the scope being
    /// resolved, e.g. its statement 0 - picks the one whose body contains it; with no
    /// position, or none containing it, null (never a guess).
    /// </summary>
    public AlMemberSyntax? FindMember(string filePath, string memberName, AlTextPosition? statementStart)
    {
        if (!_byFile.TryGetValue(NormalizePath(filePath), out var members)) return null;
        AlMemberSyntax? single = null;
        int matches = 0;
        foreach (var m in members)
        {
            if (!string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase)) continue;
            matches++;
            single = m;
            if (statementStart is { } p && m.BodyRange.ContainsStart(p)) return m;
        }
        return matches == 1 ? single : null;
    }

    /// <summary>The loops of the member <see cref="FindMember"/> resolves, or null.</summary>
    public IReadOnlyList<AlLoopSite>? FindSites(string filePath, string memberName, AlTextPosition? statementStart) =>
        FindMember(filePath, memberName, statementStart)?.Sites;

    /// <summary>Parses ONE AL file's text. <paramref name="preprocessorSymbols"/> defaults
    /// to the compiler's baseline set (CLEANSCHEMA1..25 plus any --define symbols).</summary>
    public static IReadOnlyList<AlMemberSyntax> Parse(string source, string filePath, IEnumerable<string>? preprocessorSymbols = null)
    {
        var parseOpts = new NavCA.ParseOptions(
            runtimeVersion: null!,
            preprocessorSymbols: preprocessorSymbols ?? PreprocessorSymbols(appJsonPath: null),
            documentationMode: NavCA.DocumentationMode.None);
        var tree = NavSyntax.SyntaxTree.ParseObjectText(source, path: filePath, encoding: null!, parseOpts, default);
        var root = tree.GetCompilationUnitRoot();
        var normalized = NormalizePath(filePath);

        var result = new List<AlMemberSyntax>();
        foreach (var member in root.DescendantNodes().OfType<NavSyntax.MethodOrTriggerDeclarationSyntax>())
        {
            if (member.Body == null) continue; // a declaration without a body has no statements
            var sites = new List<AlLoopSite>();
            VisitNonLoop(member.Body, parent: null, sites);
            result.Add(new AlMemberSyntax(normalized, MemberName(member), Range(member.Body), sites, CollectWrites(member.Body)));
        }
        return result;
    }

    // Same union BcCompiler.Emit builds for the bundle: CLEANSCHEMA1..25, caller --define
    // symbols, and the bundle's own app.json symbols (#1943).
    private static IReadOnlyList<string> PreprocessorSymbols(string? appJsonPath)
    {
        var manifest = BcCompiler.ReadManifestCompilerInputs(appJsonPath);
        return Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}")
            .Concat(BcCompiler.GetExtraPreprocessorSymbols())
            .Concat(manifest.PreprocessorSymbols)
            .ToList();
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string MemberName(NavSyntax.MethodOrTriggerDeclarationSyntax member) =>
        member.Name?.Identifier.ValueText ?? "?";

    // ---------------------------------------------------------------- write sets

    /// <summary>Every statement in <paramref name="body"/> (all nesting levels, blocks
    /// themselves excluded) that assigns something - see AlWriteSetModel.cs's header
    /// for what counts. Statements that write nothing are not listed.</summary>
    internal static IReadOnlyList<AlStatementWrites> CollectWrites(NavSyntax.BlockSyntax body)
    {
        var result = new List<AlStatementWrites>();
        foreach (var stmt in body.DescendantNodes().OfType<NavSyntax.StatementSyntax>())
        {
            if (stmt is NavSyntax.BlockSyntax) continue;
            var target = WriteTargetOf(stmt);
            if (target != null) result.Add(new AlStatementWrites(Start(stmt), new[] { target }));
        }
        return result;
    }

    private static string? WriteTargetOf(NavSyntax.StatementSyntax stmt) => stmt switch
    {
        NavSyntax.AssignmentStatementSyntax a => RootLocal(a.Target),
        NavSyntax.CompoundAssignmentStatementSyntax c => RootLocal(c.Target),
        // for/foreach: NOT here. The loop variable's initial value is observed at the
        // loop statement's own hit (BC assigns it before that hit), and each later pass
        // is handled by AlLoopScopeTable.LoopVariablesAssignedBefore - a write set on the
        // loop statement would only duplicate the initial value at the first body hit.
        NavSyntax.ExpressionStatementSyntax { Expression: NavSyntax.InvocationExpressionSyntax inv } => InvocationWriteTarget(inv),
        _ => null,
    };

    // `x.Add(5)` / `Rec.Insert()` write their receiver; `Clear(x)` / `Evaluate(x, s)`
    // write their first argument. A bare user-procedure call claims nothing.
    private static string? InvocationWriteTarget(NavSyntax.InvocationExpressionSyntax inv)
    {
        switch (inv.Expression)
        {
            case NavSyntax.MemberAccessExpressionSyntax ma:
                return RootLocal(ma.Expression);
            case NavSyntax.IdentifierNameSyntax id when ByRefFirstArgumentBuiltins.Contains(id.Identifier.ValueText):
                var args = inv.ArgumentList?.Arguments;
                return args is { Count: > 0 } ? RootLocal(args.Value[0]) : null;
            default:
                return null;
        }
    }

    // The local an expression ultimately names: `Rec.Amount` and `arr[1]` root at `Rec`
    // and `arr`; a parenthesised expression at whatever it wraps. Anything else (a
    // literal, a call result) names no local.
    private static string? RootLocal(SyntaxNode? expr) => expr switch
    {
        null => null,
        NavSyntax.IdentifierNameSyntax id => id.Identifier.ValueText,
        NavSyntax.MemberAccessExpressionSyntax ma => RootLocal(ma.Expression),
        NavSyntax.ElementAccessExpressionSyntax ea => RootLocal(ea.Expression),
        NavSyntax.ParenthesizedExpressionSyntax p => RootLocal(p.Expression),
        _ => null,
    };

    // ---------------------------------------------------------------- loops

    private static bool IsLoop(SyntaxNode node) =>
        node is NavSyntax.ForStatementSyntax or NavSyntax.ForEachStatementSyntax
            or NavSyntax.WhileStatementSyntax or NavSyntax.RepeatStatementSyntax;

    private static void VisitNonLoop(SyntaxNode node, int? parent, List<AlLoopSite> sites)
    {
        foreach (var child in node.ChildNodes())
        {
            if (IsLoop(child)) VisitLoop((NavSyntax.StatementSyntax)child, parent, sites);
            else VisitNonLoop(child, parent, sites);
        }
    }

    private static int VisitLoop(NavSyntax.StatementSyntax loop, int? parent, List<AlLoopSite> sites)
    {
        int index = sites.Count;
        sites.Add(null!); // reserve the slot: nested sites get higher indices than their parent

        AlLoopKind kind;
        string? loopVariable = null;
        AlTextRange header;
        IEnumerable<NavSyntax.StatementSyntax> bodyStatements;
        switch (loop)
        {
            case NavSyntax.ForStatementSyntax f:
                kind = AlLoopKind.For;
                loopVariable = RootLocal(f.LoopVariable) ?? f.LoopVariable?.ToString().Trim();
                header = new AlTextRange(Start(f), End(f.DoKeywordToken));
                bodyStatements = Flatten(f.Statement);
                break;
            case NavSyntax.ForEachStatementSyntax fe:
                kind = AlLoopKind.ForEach;
                loopVariable = RootLocal(fe.IterationVariable);
                header = new AlTextRange(Start(fe), End(fe.DoKeywordToken));
                bodyStatements = Flatten(fe.Statement);
                break;
            case NavSyntax.WhileStatementSyntax w:
                kind = AlLoopKind.While;
                header = new AlTextRange(Start(w), End(w.DoKeywordToken));
                bodyStatements = Flatten(w.Statement);
                break;
            case NavSyntax.RepeatStatementSyntax r:
                kind = AlLoopKind.Repeat;
                header = new AlTextRange(Start(r.UntilKeywordToken), r.Condition != null ? End(r.Condition) : End(r));
                bodyStatements = r.Statements.SelectMany(Flatten);
                break;
            default:
                throw new InvalidOperationException($"[iterations] not a loop statement: {loop.Kind}");
        }

        var body = new List<AlLoopBodyStatement>();
        foreach (var stmt in bodyStatements)
        {
            if (IsLoop(stmt))
            {
                int nested = VisitLoop(stmt, index, sites);
                body.Add(new AlLoopBodyStatement(Range(stmt), nested));
            }
            else
            {
                body.Add(new AlLoopBodyStatement(Range(stmt), null));
                VisitNonLoop(stmt, index, sites); // loops inside an if/case branch are children too
            }
        }

        sites[index] = new AlLoopSite(index, kind, loopVariable, Range(loop), new[] { header }, body, parent);
        return index;
    }

    // `begin ... end` is itself a statement in AL's grammar; the loop body is its statements.
    private static IEnumerable<NavSyntax.StatementSyntax> Flatten(NavSyntax.StatementSyntax? stmt)
    {
        if (stmt == null) yield break;
        if (stmt is NavSyntax.BlockSyntax block)
        {
            foreach (var s in block.Statements)
                foreach (var inner in Flatten(s))
                    yield return inner;
        }
        else
        {
            yield return stmt;
        }
    }

    // ---------------------------------------------------------------- positions

    private static AlTextPosition Start(SyntaxNode node)
    {
        var p = node.GetLocation().GetLineSpan().StartLinePosition;
        return new AlTextPosition(p.Line, p.Character);
    }

    private static AlTextPosition End(SyntaxNode node)
    {
        var p = node.GetLocation().GetLineSpan().EndLinePosition;
        return new AlTextPosition(p.Line, p.Character);
    }

    private static AlTextPosition Start(NavSyntax.SyntaxToken token)
    {
        var p = token.GetLocation().GetLineSpan().StartLinePosition;
        return new AlTextPosition(p.Line, p.Character);
    }

    private static AlTextPosition End(NavSyntax.SyntaxToken token)
    {
        var p = token.GetLocation().GetLineSpan().EndLinePosition;
        return new AlTextPosition(p.Line, p.Character);
    }

    private static AlTextRange Range(SyntaxNode node) => new(Start(node), End(node));
}
