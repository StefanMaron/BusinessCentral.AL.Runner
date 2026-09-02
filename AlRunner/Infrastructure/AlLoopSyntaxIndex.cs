// AlLoopSyntaxIndex - reads every loop statement out of a bundle's AL source with BC's
// own syntax tree (issue #2056, `iterationTracking`). This is the ONLY place loop
// STRUCTURE comes from: BC's [SourceSpans] carry file/line/column per statement but
// nothing says "these statements form a loop body", and the StmtHit stream alone cannot
// tell a nested loop's repeat from its parent's (see AlLoopModel.cs's header for why).
//
// Parsed once per `execute` request that asks for iterationTracking (HandleServerExecute),
// with the same preprocessor symbols BcCompiler.Emit uses for the bundle, so `#if`
// regions resolve the way they did when the code was compiled. Cost: one
// SyntaxTree.ParseObjectText per .al file, the same call the compiler already made -
// independent of the compile cache on purpose, since a cache HIT skips BcCompiler
// entirely and this index must still exist.
//
// Lookup is by (file, member name) - the same identity AlCoverageTracker resolves a
// scope class to (AlCoverageSourceMap for the file, [NavName] on the scope type for the
// member). Field/action triggers can share a name within one object (two fields' OnValidate),
// so FindSites disambiguates by which member body contains the scope's first statement.
using Microsoft.Dynamics.Nav.CodeAnalysis;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Infrastructure;

/// <summary>One AL trigger/procedure and the loops in its body (document order; nested
/// loops after their parent). <c>Sites</c> is empty for a member without loops - still
/// listed, so a lookup can tell "found, no loops" from "no such member".</summary>
public sealed record AlLoopMember(string FilePath, string Name, AlTextRange BodyRange, IReadOnlyList<AlLoopSite> Sites);

public sealed class AlLoopSyntaxIndex
{
    private readonly Dictionary<string, List<AlLoopMember>> _byFile = new(StringComparer.OrdinalIgnoreCase);

    private AlLoopSyntaxIndex() { }

    /// <summary>Scans every *.al file under <paramref name="roots"/> (recursively, the
    /// same walk AlCoverageSourceMap.Build does) and indexes its loops. Each root's own
    /// app.json supplies that bundle's preprocessor symbols, as in BcCompiler.</summary>
    public static AlLoopSyntaxIndex Build(IEnumerable<string> roots)
    {
        var members = new List<AlLoopMember>();
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

    public static AlLoopSyntaxIndex FromMembers(IEnumerable<AlLoopMember> members)
    {
        var index = new AlLoopSyntaxIndex();
        foreach (var m in members)
        {
            if (!index._byFile.TryGetValue(m.FilePath, out var list))
                index._byFile[m.FilePath] = list = new List<AlLoopMember>();
            list.Add(m);
        }
        return index;
    }

    /// <summary>
    /// The loops of the member named <paramref name="memberName"/> in <paramref
    /// name="filePath"/>, or null when no such member exists there. When several members
    /// share the name (field triggers), <paramref name="statementStart"/> - any statement
    /// position of the scope being resolved, e.g. its statement 0 - picks the one whose
    /// body contains it; with no position, or none containing it, null (never a guess).
    /// </summary>
    public IReadOnlyList<AlLoopSite>? FindSites(string filePath, string memberName, AlTextPosition? statementStart)
    {
        if (!_byFile.TryGetValue(NormalizePath(filePath), out var members)) return null;
        AlLoopMember? single = null;
        int matches = 0;
        foreach (var m in members)
        {
            if (!string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase)) continue;
            matches++;
            single = m;
            if (statementStart is { } p && m.BodyRange.ContainsStart(p)) return m.Sites;
        }
        return matches == 1 ? single!.Sites : null;
    }

    /// <summary>Parses ONE AL file's text. <paramref name="preprocessorSymbols"/> defaults
    /// to the compiler's baseline set (CLEANSCHEMA1..25 plus any --define symbols).</summary>
    public static IReadOnlyList<AlLoopMember> Parse(string source, string filePath, IEnumerable<string>? preprocessorSymbols = null)
    {
        var parseOpts = new NavCA.ParseOptions(
            runtimeVersion: null!,
            preprocessorSymbols: preprocessorSymbols ?? PreprocessorSymbols(appJsonPath: null),
            documentationMode: NavCA.DocumentationMode.None);
        var tree = NavSyntax.SyntaxTree.ParseObjectText(source, path: filePath, encoding: null!, parseOpts, default);
        var root = tree.GetCompilationUnitRoot();
        var normalized = NormalizePath(filePath);

        var result = new List<AlLoopMember>();
        foreach (var member in root.DescendantNodes().OfType<NavSyntax.MethodOrTriggerDeclarationSyntax>())
        {
            if (member.Body == null) continue; // a declaration without a body has no statements
            var sites = new List<AlLoopSite>();
            VisitNonLoop(member.Body, parent: null, sites);
            result.Add(new AlLoopMember(normalized, MemberName(member), Range(member.Body), sites));
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
                loopVariable = VariableName(f.LoopVariable);
                header = new AlTextRange(Start(f), End(f.DoKeywordToken));
                bodyStatements = Flatten(f.Statement);
                break;
            case NavSyntax.ForEachStatementSyntax fe:
                kind = AlLoopKind.ForEach;
                loopVariable = VariableName(fe.IterationVariable);
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

    private static string? VariableName(SyntaxNode? expr) => expr switch
    {
        null => null,
        NavSyntax.IdentifierNameSyntax id => id.Identifier.ValueText,
        _ => expr.ToString().Trim(),
    };

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
