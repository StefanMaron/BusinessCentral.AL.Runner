// #5147: which [Test] procedures reach the method an AL0132 sits in. A test usually reaches a
// missing member through a helper (Initialize, Create...) or a library codeunit, not in its own
// body, so the attribution walks the compile's call graph backwards from that method.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavDiag = Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Node = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.MethodOrTriggerDeclarationSyntax;

namespace AlRunner;

/// <summary>
/// The static call graph of one compile's procedures, built from BC's own semantic model:
/// an edge per invocation that binds to a procedure declared in the same compile, and from a
/// [Test] to each handler its [HandlerFunctions] names, and from an event publisher declared in the
/// compile to each [EventSubscriber] naming it (#5161). A publisher the compile does not declare as
/// a procedure (an event of a precompiled object) adds no edge. A table operation and Codeunit.Run start
/// a trigger and database events that no call binds to, so each is recorded as a raise of the trigger's or
/// the event's key (TddCallGraph.Triggers.cs, #5286). Calls that do not bind (the missing member itself)
/// add no edge, and a call to a
/// procedure another compile declares adds none inside this graph: it is kept as an external call,
/// which <see cref="ReachThroughDependencies"/> follows into the bundles compiled before this one.
/// A subscriber whose publisher this compile does not declare (it is in another source bundle, #5264)
/// is kept as an external subscriber: whoever raises that publisher reaches it, which
/// <see cref="TddCrossBundle.CallersOf"/> follows through the graphs of the bundles compiled before. Over-approximates execution — a branch that never runs still counts — which keeps
/// the annotation a statement about what the test's code references.
/// </summary>
internal sealed partial class TddCallGraph
{
    private readonly Dictionary<Node, List<Node>> _callers = new();
    // Invocations that bind to a procedure this compile does not declare (a dependency's), by the
    // dependency procedure's ProcKey: the way a graph reaches into another bundle (#5161).
    private readonly List<(Node Caller, string Key)> _externalCalls = new();
    // [EventSubscriber]s whose publisher procedure this compile does not declare, by the publisher's
    // ProcKey (#5264): raising it elsewhere calls them, and nothing in this graph says who raises it.
    private readonly List<(Node Subscriber, string PublisherKey)> _externalSubscribers = new();

    private TddCallGraph() { }

    public static TddCallGraph Build(NavCA.Compilation compilation, IReadOnlyList<NavSyntax.SyntaxTree> trees)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var g = new TddCallGraph();
        int invocations = 0, edges = 0, handlerEdges = 0, subscriberEdges = 0, failed = 0;
        var objects = trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<NavSyntax.ObjectSyntax>()).ToList();
        g.CollectXmlPortTables(trees);
        foreach (var tree in trees)
        {
            NavCA.SemanticModel model;
            try { model = compilation.GetSemanticModel(tree); }
            catch { failed++; continue; }
            var root = tree.GetRoot();
            g.CollectTriggers(model, root);
            failed += g.CollectTestPageAssignments(model, root);
            foreach (var inv in root.DescendantNodes().OfType<NavSyntax.InvocationExpressionSyntax>())
            {
                invocations++;
                try
                {
                    var caller = EnclosingMethod(inv);
                    if (caller == null) continue;
                    var bound = BoundMethod(model, inv);
                    var declared = Declaration(bound);
                    if (g.AddEdge(caller, declared)) edges++;
                    if (declared == null && bound?.ContainingType is { } owner && bound.Name.Length > 0)
                        g._externalCalls.Add((caller, ProcKey(owner.Name, bound.Name)));
                    if (declared == null) g.AddEntryPointCalls(model, inv, caller);
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
            // [EventSubscriber(...)]: raising the event calls the subscriber, so the publisher's
            // declaration is a caller of it, and whoever invokes the publisher reaches it.
            foreach (var method in root.DescendantNodes().OfType<NavSyntax.MethodDeclarationSyntax>())
            {
                if (Subscription(method) is not { } sub) continue;
                var declared = false;
                foreach (var publisher in PublisherMethods(sub, objects))
                {
                    declared = true;
                    if (g.AddEdge(publisher, method)) subscriberEdges++;
                }
                if (!declared) g._externalSubscribers.Add((method, ProcKey(sub.ObjectName, sub.EventName)));
            }
        }
        sw.Stop();
        PerfTrace.Log($"tdd call graph: {trees.Count} tree(s), {invocations} invocation(s), {edges} call edge(s), " +
            $"{handlerEdges} handler edge(s), {subscriberEdges} subscriber edge(s), {g._externalSubscribers.Count} external subscriber(s), " +
            $"{failed} bind failure(s), {sw.ElapsedMilliseconds} ms");
        if (failed > 0)
            Console.Error.WriteLine($"--tdd: {failed} call(s) could not be bound while finding the tests that reach " +
                "generated members; a test reaching one only through such a call carries no generatedStubs.");
        return g;
    }

    private bool AddEdge(Node caller, Node? callee)
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

    /// <summary>What an [EventSubscriber(ObjectType::X, X::"Name", 'Event', ...)] names: the publisher's
    /// object kind and name and the event procedure. Null for a method with no such attribute, and for
    /// a publisher named by a bare object id: there is no `X::` to read, and `X::Id` is a syntax error
    /// in AL (#5245).</summary>
    private readonly record struct SubscriptionTarget(string Kind, string ObjectName, string EventName);

    private static SubscriptionTarget? Subscription(NavSyntax.MethodDeclarationSyntax subscriber)
    {
        var attr = subscriber.Attributes.FirstOrDefault(a =>
            string.Equals(Name(a.Name), "EventSubscriber", StringComparison.OrdinalIgnoreCase));
        if (attr == null) return null;
        var text = attr.ToString();
        int open = text.IndexOf('('), close = text.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        var args = SplitArguments(text[(open + 1)..close]);
        if (args.Count < 3) return null;

        var kind = AfterScope(args[0]).ToLowerInvariant();
        var target = args[1].Contains("::") ? args[1][(args[1].IndexOf("::", StringComparison.Ordinal) + 2)..].Trim() : "";
        var eventName = Unquote(args[2].Trim());
        if (eventName.Length == 0 || target.Length == 0) return null;
        if (kind == "database") kind = "table";
        var objectName = Unquote(target.Contains('.') && !target.StartsWith('"') ? target[(target.LastIndexOf('.') + 1)..] : target);
        return new SubscriptionTarget(kind, objectName, eventName);
    }

    /// <summary>The procedure a subscription names, when this compile declares it. A table's built-in
    /// events and an object this compile does not hold have no procedure to be a caller, so they yield
    /// nothing.</summary>
    private static IEnumerable<NavSyntax.MethodDeclarationSyntax> PublisherMethods(
        SubscriptionTarget sub, IReadOnlyList<NavSyntax.ObjectSyntax> objects)
    {
        foreach (var obj in objects)
        {
            if (!ObjectKind(obj).Equals(sub.Kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Name(obj.Name).Equals(sub.ObjectName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var m in obj.DescendantNodes().OfType<NavSyntax.MethodDeclarationSyntax>())
                if (Name(m.Name).Equals(sub.EventName, StringComparison.OrdinalIgnoreCase))
                    yield return m;
        }
    }

    private static string ObjectKind(NavSyntax.ObjectSyntax obj)
    {
        var n = obj.GetType().Name;
        return n.EndsWith("Syntax", StringComparison.Ordinal) ? n[..^"Syntax".Length] : n;
    }

    private static string AfterScope(string s)
    {
        s = s.Trim();
        var i = s.IndexOf("::", StringComparison.Ordinal);
        return i < 0 ? s : s[(i + 2)..].Trim();
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;
    }

    /// <summary>Splits an attribute's argument text on the commas outside quotes.</summary>
    private static List<string> SplitArguments(string text)
    {
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; }
            else if (c == '"' || c == '\'') { quote = c; sb.Append(c); }
            else if (c == ',') { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>Names a procedure across compiles: object name and procedure name, case-insensitive. The
    /// kind and the parameter list are not part of it, so two overloads (or a table and a codeunit
    /// with one name) share a key, which can only over-approximate the reach (#5161).</summary>
    internal static string ProcKey(string objectName, string procedureName)
        => $"{objectName}|{procedureName}".ToLowerInvariant();

    private string? KeyOf(Node method)
    {
        for (NavCA.SyntaxNode? n = method; n != null; n = n.Parent)
            if (n is NavSyntax.ObjectSyntax o)
            {
                var objName = KeyObjectName(o, method);
                var methodName = Name(method.Name);
                return objName.Length == 0 || methodName.Length == 0 ? null : ProcKey(objName, methodName);
            }
        return null;
    }

    /// <summary>The key of every procedure of this compile that is, or transitively calls, the one
    /// containing <paramref name="diag"/>: where another bundle's code enters a generated member. A
    /// subscriber on the way adds its publisher's key and the key of everything known to raise it
    /// (#5264), which are not procedures of this compile.</summary>
    public IReadOnlyList<string> ProcedureKeysReaching(NavDiag.Diagnostic diag)
    {
        var tree = diag.Location.SourceTree;
        if (tree == null) return Array.Empty<string>();
        var start = EnclosingMethod(tree.GetRoot().FindToken(diag.Location.SourceSpan.Start).Parent);
        if (start == null) return Array.Empty<string>();
        var keys = new List<string>();
        var reached = ReachClosure(new[] { start }, out var raisers);
        foreach (var m in reached)
            if (KeyOf(m) is { } k && !keys.Contains(k)) keys.Add(k);
        foreach (var k in raisers)
            if (!keys.Contains(k)) keys.Add(k);
        return keys;
    }

    /// <summary>
    /// The procedures and [Test]s of this compile that reach a generated member through a
    /// dependency's procedure, given the dependency procedures that reach one
    /// (<paramref name="membersOf"/>, by ProcKey). Each procedure carries the keys it can be
    /// entered by, so a later bundle that calls it is followed the same way (#5161).
    /// </summary>
    public (List<(string Key, TddGeneratedMember Member)> Procedures, List<(string TestLabel, TddGeneratedMember Member)> Tests)
        ReachThroughDependencies(Func<string, IReadOnlyList<TddGeneratedMember>> membersOf)
    {
        var procedures = new List<(string, TddGeneratedMember)>();
        var tests = new List<(string, TddGeneratedMember)>();
        var byMember = new Dictionary<TddGeneratedMember, List<Node>>();
        foreach (var (caller, key) in _externalCalls)
            foreach (var member in membersOf(key))
            {
                if (!byMember.TryGetValue(member, out var seeds)) byMember[member] = seeds = new();
                if (!seeds.Contains(caller)) seeds.Add(caller);
            }
        foreach (var (member, seeds) in byMember)
        {
            var reached = ReachClosure(seeds, out var raisers);
            foreach (var m in reached)
            {
                if (KeyOf(m) is { } k) procedures.Add((k, member));
                if (TestLabel(m) is { } label) tests.Add((label, member));
            }
            foreach (var k in raisers) procedures.Add((k, member));
        }
        return (procedures, tests);
    }

    /// <summary>Every procedure that is, or transitively calls, one of <paramref name="seeds"/>.</summary>
    private List<Node> Closure(IEnumerable<Node> seeds)
    {
        var seen = new HashSet<Node>();
        var order = new List<Node>();
        var queue = new Queue<Node>();
        foreach (var s in seeds)
            if (seen.Add(s)) { order.Add(s); queue.Enqueue(s); }
        while (queue.Count > 0)
        {
            var m = queue.Dequeue();
            if (!_callers.TryGetValue(m, out var callers)) continue;
            foreach (var c in callers)
                if (seen.Add(c)) { order.Add(c); queue.Enqueue(c); }
        }
        return order;
    }

    /// <summary><see cref="Closure"/>, plus what a subscriber of a publisher declared in ANOTHER bundle
    /// adds (#5264): raising that publisher calls the subscriber, so every procedure of this compile
    /// that calls the publisher or anything known to raise it
    /// (<see cref="TddCrossBundle.CallersOf"/>, from the bundles compiled before this one) reaches the
    /// seeds as well, and so do the callers of those. <paramref name="raisers"/> is the key of the
    /// publisher and of each of its known raisers: a bundle compiled after this one enters through
    /// those. Over-approximates like every edge here: a subscriber with manual binding, or a
    /// publisher no path of the run raises, still counts.</summary>
    private List<Node> ReachClosure(
        IEnumerable<Node> seeds, out HashSet<string> raisers)
    {
        var order = Closure(seeds);
        raisers = new HashSet<string>(StringComparer.Ordinal);
        if (_externalSubscribers.Count == 0 && _triggers.Count == 0) return order;
        var seen = new HashSet<Node>(order);
        while (true)
        {
            var more = new List<Node>();
            foreach (var (subscriber, publisherKey) in _externalSubscribers.Concat(_triggers))
            {
                if (!seen.Contains(subscriber)) continue;
                foreach (var raiser in RaiseKeys(publisherKey).SelectMany(TddCrossBundle.CallersOf))
                {
                    if (!raisers.Add(raiser)) continue;
                    foreach (var (caller, key) in _externalCalls)
                        if (key == raiser && !seen.Contains(caller)) more.Add(caller);
                }
            }
            if (more.Count == 0) return order;
            foreach (var m in Closure(more))
                if (seen.Add(m)) order.Add(m);
        }
    }

    /// <summary>The edges of this graph by <see cref="ProcKey"/>, as (callee, caller): what a bundle
    /// compiled later needs to follow a subscriber of one of this bundle's publishers back to the
    /// procedures that raise it (#5264). A subscriber of a publisher declared elsewhere is an edge from
    /// the publisher's key.</summary>
    public IEnumerable<(string Callee, string Caller)> KeyEdges()
    {
        foreach (var (callee, callers) in _callers)
            if (KeyOf(callee) is { } calleeKey)
                foreach (var caller in callers)
                    if (KeyOf(caller) is { } callerKey) yield return (calleeKey, callerKey);
        foreach (var (caller, key) in _externalCalls)
            if (KeyOf(caller) is { } callerKey) yield return (key, callerKey);
        foreach (var (subscriber, publisherKey) in _externalSubscribers)
            if (KeyOf(subscriber) is { } subscriberKey) yield return (subscriberKey, publisherKey);
    }

    /// <summary>"ObjectName.MethodName" of every [Test] procedure that is, or transitively calls,
    /// the procedure containing <paramref name="diag"/>, or raises what it subscribes to or is
    /// (<see cref="ReachClosure"/>: a database event or trigger of a table operation in this compile, #5286).
    /// A test of another bundle that raises it is named by that bundle's compile.</summary>
    public IReadOnlyList<string> TestsReaching(NavDiag.Diagnostic diag)
    {
        var tree = diag.Location.SourceTree;
        if (tree == null) return Array.Empty<string>();
        var start = EnclosingMethod(tree.GetRoot().FindToken(diag.Location.SourceSpan.Start).Parent);
        if (start == null) return Array.Empty<string>();

        var labels = new List<string>();
        foreach (var m in ReachClosure(new[] { start }, out _))
            if (TestLabel(m) is { } label && !labels.Contains(label)) labels.Add(label);
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

    private static Node? Declaration(NavCA.IMethodSymbol? method)
    {
        var loc = method?.Location;
        if (loc?.SourceTree == null) return null;
        return EnclosingMethod(loc.SourceTree.GetRoot().FindToken(loc.SourceSpan.Start).Parent);
    }

    private static Node? EnclosingMethod(NavCA.SyntaxNode? node)
    {
        for (var n = node; n != null; n = n.Parent)
            if (n is Node m) return m;
        return null;
    }

    private static string? TestLabel(Node method)
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
