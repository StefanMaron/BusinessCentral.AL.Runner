// Per-member facts from BC's AL syntax tree (#2056): loops (AlLoopSite) for
// iterationTracking and statement write sets (AlStatementWrites) for captureValues.
// Parsed once per execute request with the bundle's preprocessor symbols, independent
// of the compile cache. Lookup is by (file, member name); same-named triggers are told
// apart by statement position.
using Microsoft.Dynamics.Nav.CodeAnalysis;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Infrastructure;

/// <summary>One trigger/procedure: its loops and assigning statements. Both empty for a member without any.</summary>
public sealed record AlMemberSyntax(
    string FilePath,
    string Name,
    AlTextRange BodyRange,
    IReadOnlyList<AlLoopSite> Sites,
    IReadOnlyList<AlStatementWrites> Writes);

public sealed class AlMemberSyntaxIndex
{
    // Built-ins whose first argument is by reference.
    private static readonly HashSet<string> ByRefFirstArgumentBuiltins =
        new(StringComparer.OrdinalIgnoreCase) { "Clear", "Evaluate" };

    private readonly Dictionary<string, List<AlMemberSyntax>> _byFile = new(StringComparer.OrdinalIgnoreCase);

    private AlMemberSyntaxIndex() { }

    /// <summary>Indexes every *.al file under the roots, with each root's app.json preprocessor symbols.</summary>
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

    /// <summary>The named member in the file; among same-named triggers, the one whose body contains the position. Null rather than a guess.</summary>
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

    /// <summary>The loops of the member FindMember resolves, or null.</summary>
    public IReadOnlyList<AlLoopSite>? FindSites(string filePath, string memberName, AlTextPosition? statementStart) =>
        FindMember(filePath, memberName, statementStart)?.Sites;

    /// <summary>Parses one file. Symbols default to the compiler's baseline set.</summary>
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

    // Same union BcCompiler.Emit uses.
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

    /// <summary>Every assigning statement in the body, all nesting levels; see AlWriteSetModel.cs for what counts.</summary>
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
        // for/foreach loop variables are handled by AlLoopScopeTable.LoopVariablesAssignedBefore.
        NavSyntax.ExpressionStatementSyntax { Expression: NavSyntax.InvocationExpressionSyntax inv } => InvocationWriteTarget(inv),
        _ => null,
    };

    // A method-call statement writes its receiver; Clear/Evaluate write their first argument.
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

    // The local an expression names: `Rec.Amount` and `arr[1]` root at Rec and arr.
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

    // A begin..end block is a statement in AL; the body is its statements.
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
