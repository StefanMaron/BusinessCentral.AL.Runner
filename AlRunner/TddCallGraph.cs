// #5147: which [Test] procedures reach the method an AL0132 sits in. A test usually reaches a
// missing member through a helper (Initialize, Create...) or a library codeunit, not in its own
// body, so the attribution walks the compile's call graph backwards from that method.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavDiag = Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace AlRunner;

/// <summary>
/// The static call graph of one compile's procedures, built from BC's own semantic model:
/// an edge per invocation that binds to a procedure declared in the same compile, and from a
/// [Test] to each handler its [HandlerFunctions] names. Event subscribers are not followed. Calls that
/// do not bind (the missing member itself, a precompiled dependency, a codeunit run by id) add
/// no edge. Over-approximates execution — a branch that never runs still counts — which keeps
/// the annotation a statement about what the test's code references.
/// </summary>
internal sealed class TddCallGraph
{
    private readonly Dictionary<NavSyntax.MethodDeclarationSyntax, List<NavSyntax.MethodDeclarationSyntax>> _callers = new();

    private TddCallGraph() { }

    public static TddCallGraph Build(NavCA.Compilation compilation, IReadOnlyList<NavSyntax.SyntaxTree> trees)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var g = new TddCallGraph();
        int invocations = 0, edges = 0, handlerEdges = 0, failed = 0;
        foreach (var tree in trees)
        {
            NavCA.SemanticModel model;
            try { model = compilation.GetSemanticModel(tree); }
            catch { failed++; continue; }
            var root = tree.GetRoot();
            foreach (var inv in root.DescendantNodes().OfType<NavSyntax.InvocationExpressionSyntax>())
            {
                invocations++;
                try
                {
                    var caller = EnclosingMethod(inv);
                    if (caller == null) continue;
                    if (g.AddEdge(caller, Declaration(BoundMethod(model, inv)))) edges++;
                }
                catch
                {
                    failed++;
                }
            }
            // [HandlerFunctions('A,B')] on a [Test]: the platform calls those procedures of the same
            // codeunit while the test runs, so they are callees of the test.
            foreach (var method in root.DescendantNodes().OfType<NavSyntax.MethodDeclarationSyntax>())
                foreach (var handler in HandlerMethods(method))
                    if (g.AddEdge(method, handler)) handlerEdges++;
        }
        sw.Stop();
        PerfTrace.Log($"tdd call graph: {trees.Count} tree(s), {invocations} invocation(s), {edges} call edge(s), " +
            $"{handlerEdges} handler edge(s), {failed} bind failure(s), {sw.ElapsedMilliseconds} ms");
        if (failed > 0)
            Console.Error.WriteLine($"--tdd: {failed} call(s) could not be bound while finding the tests that reach " +
                "generated members; a test reaching one only through such a call carries no generatedStubs.");
        return g;
    }

    private bool AddEdge(NavSyntax.MethodDeclarationSyntax caller, NavSyntax.MethodDeclarationSyntax? callee)
    {
        if (callee == null || ReferenceEquals(callee, caller)) return false;
        if (!_callers.TryGetValue(callee, out var list)) _callers[callee] = list = new();
        if (list.Contains(caller)) return false;
        list.Add(caller);
        return true;
    }

    private static IEnumerable<NavSyntax.MethodDeclarationSyntax> HandlerMethods(NavSyntax.MethodDeclarationSyntax test)
    {
        var attr = test.Attributes.FirstOrDefault(a =>
            string.Equals(Name(a.Name), "HandlerFunctions", StringComparison.OrdinalIgnoreCase));
        if (attr == null) yield break;
        var text = attr.ToString();
        int open = text.IndexOf('\''), close = text.LastIndexOf('\'');
        if (open < 0 || close <= open) yield break;
        var names = text[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        NavCA.SyntaxNode? obj = test.Parent;
        while (obj != null && obj is not NavSyntax.ObjectSyntax) obj = obj.Parent;
        if (obj == null) yield break;
        foreach (var m in obj.DescendantNodes().OfType<NavSyntax.MethodDeclarationSyntax>())
            if (names.Any(n => string.Equals(n, Name(m.Name), StringComparison.OrdinalIgnoreCase)))
                yield return m;
    }

    /// <summary>"ObjectName.MethodName" of every [Test] procedure that is, or transitively calls,
    /// the procedure containing <paramref name="diag"/>.</summary>
    public IReadOnlyList<string> TestsReaching(NavDiag.Diagnostic diag)
    {
        var tree = diag.Location.SourceTree;
        if (tree == null) return Array.Empty<string>();
        var start = EnclosingMethod(tree.GetRoot().FindToken(diag.Location.SourceSpan.Start).Parent);
        if (start == null) return Array.Empty<string>();

        var labels = new List<string>();
        var seen = new HashSet<NavSyntax.MethodDeclarationSyntax> { start };
        var queue = new Queue<NavSyntax.MethodDeclarationSyntax>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var m = queue.Dequeue();
            if (TestLabel(m) is { } label && !labels.Contains(label)) labels.Add(label);
            if (!_callers.TryGetValue(m, out var callers)) continue;
            foreach (var c in callers)
                if (seen.Add(c)) queue.Enqueue(c);
        }
        return labels;
    }

    private static NavCA.IMethodSymbol? BoundMethod(NavCA.SemanticModel model, NavSyntax.InvocationExpressionSyntax inv)
    {
        var a = model.GetSymbolInfo(inv);
        var b = model.GetSymbolInfo(inv.Expression);
        return a.Symbol as NavCA.IMethodSymbol
            ?? b.Symbol as NavCA.IMethodSymbol
            ?? (a.CandidateSymbols.Length == 1 ? a.CandidateSymbols[0] as NavCA.IMethodSymbol : null)
            ?? (b.CandidateSymbols.Length == 1 ? b.CandidateSymbols[0] as NavCA.IMethodSymbol : null);
    }

    private static NavSyntax.MethodDeclarationSyntax? Declaration(NavCA.IMethodSymbol? method)
    {
        var loc = method?.Location;
        if (loc?.SourceTree == null) return null;
        return EnclosingMethod(loc.SourceTree.GetRoot().FindToken(loc.SourceSpan.Start).Parent);
    }

    private static NavSyntax.MethodDeclarationSyntax? EnclosingMethod(NavCA.SyntaxNode? node)
    {
        for (var n = node; n != null; n = n.Parent)
            if (n is NavSyntax.MethodDeclarationSyntax m) return m;
        return null;
    }

    private static string? TestLabel(NavSyntax.MethodDeclarationSyntax method)
    {
        if (!method.Attributes.Any(a => string.Equals(Name(a.Name), "Test", StringComparison.OrdinalIgnoreCase)))
            return null;
        NavSyntax.ObjectSyntax? obj = null;
        for (NavCA.SyntaxNode? n = method; n != null; n = n.Parent)
            if (n is NavSyntax.ObjectSyntax o) { obj = o; break; }
        if (obj == null) return null;
        var objName = Name(obj.Name);
        var methodName = Name(method.Name);
        return objName.Length == 0 || methodName.Length == 0 ? null : $"{objName}.{methodName}";
    }

    private static string Name(NavSyntax.IdentifierNameSyntax? id)
    {
        var s = id == null ? "" : (id.Identifier.ValueText ?? id.Identifier.Text ?? "");
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }
}
