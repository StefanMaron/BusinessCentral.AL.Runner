// --tdd for a procedure the test calls on a codeunit that arrives precompiled (#5037's last half): the
// implementing app is a package (.app symbols, a DLL or embedded source) and not a source bundle of
// the run, so there is no source to add the member to, and nothing precompiled may be changed
// (precompiled-dll-respect.md: no existing member's body, no signature, no type of a loaded app).
//
// The member is therefore generated BESIDE the object, in the test's own compile: a new codeunit that
// exists for this run only holds the stub, and each call site that names the missing member is pointed
// at a variable of that codeunit. Every other call through the original variable keeps running the real
// object. Text edits on the in-memory tree, never a file on disk. See docs/tdd-precompiled.md.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner;

internal static class TddPrecompiledStub
{
    /// <summary>One call site of a missing procedure of a precompiled codeunit.</summary>
    internal readonly record struct Site(
        int TreeIdx, NavSyntax.MemberAccessExpressionSyntax Mae, NavSyntax.CodeunitSyntax Container);

    /// <summary>The stub codeunit one precompiled object gets in ONE FILE of the compile, and what goes in it.
    /// Per file, never shared: a file the compile drops takes its stub with it, and a call site in another file
    /// must not be left pointing at a stub that is gone.</summary>
    internal sealed class Group
    {
        public int StubId;
        public int TreeIdx;
        public readonly List<string> Procedures = new();
        public readonly List<Site> Sites = new();
    }

    /// <summary>
    /// The call site of <paramref name="mae"/>, in <paramref name="tree"/> (the tree its diagnostic is in), when it can be pointed at a stub variable: the member
    /// is called through a plain variable (the rewrite skips evaluating the qualifier, so anything else
    /// could drop a side effect) inside a codeunit (a variable is added to the object). Null otherwise.
    /// </summary>
    internal static Site? SiteOf(NavSyntax.SyntaxTree[] originalTrees, NavSyntax.SyntaxTree? tree,
        NavSyntax.MemberAccessExpressionSyntax? mae)
    {
        if (mae == null || mae.Expression is not NavSyntax.IdentifierNameSyntax) return null;
        var treeIdx = Array.IndexOf(originalTrees, tree);
        if (treeIdx < 0) return null;
        var container = mae.Ancestors().OfType<NavSyntax.CodeunitSyntax>().FirstOrDefault();
        return container == null ? null : new Site(treeIdx, mae, container);
    }

    /// <summary>The first object id of <paramref name="idRanges"/> that no codeunit of <paramref name="trees"/>
    /// uses and <paramref name="reserved"/> does not hold.</summary>
    internal static int? FreeCodeunitId(NavSyntax.SyntaxTree[] trees, IReadOnlyList<(int From, int To)> idRanges,
        ISet<int> reserved)
    {
        var used = new HashSet<int>(reserved);
        foreach (var t in trees)
            if (t.GetRoot() is NavSyntax.CompilationUnitSyntax root)
                foreach (var o in root.Objects)
                    if (o is NavSyntax.CodeunitSyntax cu && cu.ObjectId?.Value.Value is int id) used.Add(id);
        foreach (var (from, to) in idRanges)
            for (var id = from; id <= to; id++)
                if (!used.Contains(id)) return id;
        return null;
    }

    /// <summary>The <c>idRanges</c> of an app.json; empty when it is absent, unreadable or declares none.</summary>
    internal static IReadOnlyList<(int From, int To)> ReadIdRanges(string? appJsonPath)
    {
        var ranges = new List<(int, int)>();
        if (appJsonPath == null || !File.Exists(appJsonPath)) return ranges;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonPath));
            if (json.RootElement.TryGetProperty("idRanges", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var r in arr.EnumerateArray())
                    if (r.TryGetProperty("from", out var f) && r.TryGetProperty("to", out var t)
                        && f.TryGetInt32(out var from) && t.TryGetInt32(out var to))
                        ranges.Add((from, to));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { }
        return ranges;
    }

    internal static string StubName(int id) => $"TDD Stub {id}";
    internal static string VarName(int id) => $"TddStub{id}";

    /// <summary>
    /// Points every site of every group at its stub codeunit, in <paramref name="trees"/>. The stub
    /// codeunit is appended to the group's file; each containing codeunit gets one variable of it. All edits are computed against the trees as parsed, then applied back to front,
    /// so no offset moves under a later edit of the same tree.
    /// </summary>
    internal static void Apply(NavSyntax.SyntaxTree[] trees, NavCA.ParseOptions parseOptions, IReadOnlyList<Group> groups)
    {
        var edits = new Dictionary<int, List<(int Start, int Length, string Text)>>();
        void Edit(int treeIdx, int start, int length, string text)
        {
            if (!edits.TryGetValue(treeIdx, out var l)) edits[treeIdx] = l = new();
            l.Add((start, length, text));
        }

        var appended = new Dictionary<int, System.Text.StringBuilder>();
        foreach (var g in groups)
        {
            var variable = VarName(g.StubId);
            var declared = new HashSet<(int, int)>();
            foreach (var s in g.Sites)
            {
                Edit(s.TreeIdx, s.Mae.Expression.Span.Start, s.Mae.Expression.Span.Length, variable);
                if (declared.Add((s.TreeIdx, s.Container.Span.Start)))
                    Edit(s.TreeIdx, s.Container.CloseBraceToken.SpanStart, 0,
                        $"    var\n        {variable}: Codeunit {Quote(StubName(g.StubId))};\n");
            }
            if (!appended.TryGetValue(g.TreeIdx, out var sb)) appended[g.TreeIdx] = sb = new();
            sb.Append($"\ncodeunit {g.StubId} {Quote(StubName(g.StubId))}\n{{\n");
            foreach (var p in g.Procedures) sb.Append($"    {p}\n");
            sb.Append("}\n");
        }
        foreach (var (idx, sb) in appended)
            Edit(idx, trees[idx].GetText().Length, 0, sb.ToString());

        foreach (var (idx, list) in edits)
        {
            var text = trees[idx].GetText().ToString();
            // Back to front; at an equal start the later-added edit goes first, so insertions keep their order.
            foreach (var (start, length, repl) in list.Select((e, i) => (e, i)).OrderByDescending(x => x.e.Start).ThenByDescending(x => x.i).Select(x => x.e))
                text = text.Substring(0, start) + repl + text.Substring(start + length);
            trees[idx] = NavSyntax.SyntaxTree.ParseObjectText(text, path: trees[idx].FilePath, encoding: null!, parseOptions, default);
        }
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
}
