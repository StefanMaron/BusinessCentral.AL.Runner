using System.Numerics;

namespace CallGraphSpike;

sealed class GraphOptions
{
    public HashSet<string> DisabledWildcards = new(StringComparer.OrdinalIgnoreCase);
    public bool GlobalRoots = true;
    public bool TableRelation = true;
    public bool ProcedureLevelTests = true;
    public bool PreciseRecordOps; // procedure graph: Insert/Modify/Delete/Rename/Validate(Field) reach only the matching triggers
    public HashSet<string> DroppedEdges = new(StringComparer.OrdinalIgnoreCase); // ablation: edge kinds (EdgeCounts keys) left out
}

/// <summary>
/// Object-level reachability graph. Nodes are AL objects plus one wildcard node per object kind
/// ("*Codeunit" etc.) standing for "every object of that kind", which non-constant dynamic
/// dispatch (Codeunit.Run(Id), Report.Run(Id), RecordRef.Open(Id), ...) points at.
/// </summary>
sealed class Graph
{
    public static readonly string[] WildKinds = { "Table", "Page", "Codeunit", "Report", "Query", "XmlPort" };
    public readonly List<AlObject> Objs;
    public readonly int N;           // object nodes; wildcard nodes are N..N+WildKinds.Length-1
    public int Total => N + WildKinds.Length;
    readonly List<int>[] _adj;
    readonly Dictionary<(string, string), List<int>> _byName = new();
    readonly Dictionary<(string, int), List<int>> _byId = new();
    readonly GraphOptions _opt;
    public readonly List<int> GlobalRoots = new();
    public readonly Dictionary<string, int> EdgeCounts = new();
    public readonly Dictionary<string, int> Unresolved = new();
    public int UnresolvedPublishers;
    ulong[][] _reach = Array.Empty<ulong[]>();
    int _words;

    public Graph(List<AlObject> objs, GraphOptions opt)
    {
        Objs = objs; N = objs.Count; _opt = opt;
        _adj = new List<int>[Total];
        for (var i = 0; i < Total; i++) _adj[i] = new List<int>();
        for (var i = 0; i < N; i++)
        {
            var o = objs[i];
            Add(_byName, (o.Kind, o.Name), i);
            if (o.Id != 0) Add(_byId, (o.Kind, o.Id), i);
        }
        Build();
        ComputeReach();
    }

    static void Add<TK>(Dictionary<TK, List<int>> d, TK k, int v) where TK : notnull
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new List<int>();
        l.Add(v);
    }

    public int WildNode(string kind) => N + Array.IndexOf(WildKinds, kind);

    public IEnumerable<int> Resolve(Ref r, bool countMiss = true)
    {
        if (r.Kind == "EnumOpt")
            return _byName.TryGetValue(("Enum", r.Name), out var e) ? e : Enumerable.Empty<int>();
        var kinds = r.Kind == "*" ? Extract.BaseKinds : new[] { r.Kind };
        var res = new List<int>();
        foreach (var k in kinds)
        {
            if (r.Id != 0) { if (_byId.TryGetValue((k, r.Id), out var l)) res.AddRange(l); }
            else if (_byName.TryGetValue((k, r.Name), out var l)) res.AddRange(l);
        }
        if (res.Count == 0 && countMiss)
        {
            var key = r.Kind + ":" + r.Via;
            Unresolved[key] = Unresolved.GetValueOrDefault(key) + 1;
        }
        return res;
    }

    void Edge(int a, int b, string via)
    {
        if (a == b || _opt.DroppedEdges.Contains(via)) return;
        _adj[a].Add(b);
        EdgeCounts[via] = EdgeCounts.GetValueOrDefault(via) + 1;
    }

    public List<int> ResolveRefs(IEnumerable<Ref> refs)
    {
        var res = new List<int>();
        foreach (var r in refs)
        {
            if (!_opt.TableRelation && r.Via == "tablerelation") continue;
            res.AddRange(Resolve(r));
        }
        return res;
    }

    void Build()
    {
        for (var i = 0; i < N; i++)
        {
            var o = Objs[i];
            foreach (var r in o.Refs)
            {
                if (!_opt.TableRelation && r.Via == "tablerelation") continue;
                foreach (var t in Resolve(r)) Edge(i, t, "ref:" + r.Via);
            }
            foreach (var (kind, _) in o.Wildcards)
                if (!_opt.DisabledWildcards.Contains(kind)) Edge(i, WildNode(kind), "wildcard:" + kind);
            if (o.ExtendsName != null)
            {
                var baseKind = o.Kind[..^"Extension".Length];
                foreach (var b in Resolve(new Ref(baseKind, o.ExtendsName, 0, "extends")))
                { Edge(b, i, "base->extension"); Edge(i, b, "extension->base"); }
            }
            foreach (var iface in o.Implements)
                foreach (var b in Resolve(new Ref("Interface", iface, 0, "implements")))
                    Edge(b, i, "interface->implementation");
            foreach (var s in o.Subscribes)
            {
                var pubs = Resolve(s, countMiss: false).ToList();
                if (pubs.Count == 0) { UnresolvedPublishers++; if (_opt.GlobalRoots) GlobalRoots.Add(i); continue; }
                foreach (var p in pubs)
                {
                    Edge(p, i, "publisher->subscriber");
                    if (_opt.GlobalRoots && Objs[p].App == "System") GlobalRoots.Add(i);
                }
            }
        }
        for (var w = 0; w < WildKinds.Length; w++)
            for (var i = 0; i < N; i++)
                if (Objs[i].Kind == WildKinds[w]) Edge(N + w, i, "wildcard->all");
        var distinct = GlobalRoots.Distinct().ToList();
        GlobalRoots.Clear(); GlobalRoots.AddRange(distinct);
    }

    // Tarjan SCC (iterative), then per-SCC reachability bitsets in emission (reverse topological) order.
    void ComputeReach()
    {
        var n = Total;
        _words = (n + 63) / 64;
        var index = new int[n]; Array.Fill(index, -1);
        var low = new int[n]; var onStack = new bool[n]; var comp = new int[n];
        var stack = new Stack<int>(); var sccs = new List<List<int>>();
        var idx = 0;
        var callStack = new Stack<(int v, int ei)>();
        for (var s = 0; s < n; s++)
        {
            if (index[s] != -1) continue;
            callStack.Push((s, 0));
            index[s] = low[s] = idx++; stack.Push(s); onStack[s] = true;
            while (callStack.Count > 0)
            {
                var (v, ei) = callStack.Pop();
                if (ei < _adj[v].Count)
                {
                    callStack.Push((v, ei + 1));
                    var w = _adj[v][ei];
                    if (index[w] == -1)
                    {
                        index[w] = low[w] = idx++; stack.Push(w); onStack[w] = true;
                        callStack.Push((w, 0));
                    }
                    else if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                }
                else
                {
                    if (low[v] == index[v])
                    {
                        var c = new List<int>();
                        int w;
                        do { w = stack.Pop(); onStack[w] = false; comp[w] = sccs.Count; c.Add(w); } while (w != v);
                        sccs.Add(c);
                    }
                    if (callStack.Count > 0)
                    {
                        var (u, _) = callStack.Peek();
                        low[u] = Math.Min(low[u], low[v]);
                    }
                }
            }
        }
        var sccReach = new ulong[sccs.Count][];
        for (var c = 0; c < sccs.Count; c++)
        {
            var bits = new ulong[_words];
            foreach (var v in sccs[c])
            {
                bits[v >> 6] |= 1UL << (v & 63);
                foreach (var w in _adj[v])
                {
                    var cw = comp[w];
                    if (cw == c) continue;
                    var ob = sccReach[cw];
                    for (var i = 0; i < _words; i++) bits[i] |= ob[i];
                }
            }
            sccReach[c] = bits;
        }
        _reach = new ulong[n][];
        for (var v = 0; v < n; v++) _reach[v] = sccReach[comp[v]];
        SccCount = sccs.Count;
        LargestScc = sccs.Max(s => s.Count);
    }

    public int SccCount, LargestScc;
    public int Words => _words;
    public ulong[] ReachOf(int node) => _reach[node];

    public ulong[] Closure(IEnumerable<int> roots)
    {
        var bits = new ulong[_words];
        foreach (var r in roots)
        {
            var rb = _reach[r];
            for (var i = 0; i < _words; i++) bits[i] |= rb[i];
        }
        return bits;
    }

    public static bool Has(ulong[] bits, int v) => (bits[v >> 6] & (1UL << (v & 63))) != 0;
    public static int Count(ulong[] bits) { var c = 0; foreach (var w in bits) c += BitOperations.PopCount(w); return c; }

    /// <summary>
    /// Root nodes for one test method: the objects named by the test procedure, by every procedure
    /// of its codeunit it can call by name (transitively), by the global variables those use, by its
    /// declared handler functions, and by the codeunit's triggers and event subscribers. Plus the
    /// global roots (subscribers to platform-raised events).
    /// </summary>
    public List<int> TestRoots(AlObject cu, Proc test, out List<string> wildKinds)
    {
        var refs = new List<Ref>();
        var wild = new HashSet<string>();
        if (!_opt.ProcedureLevelTests)
        {
            refs.AddRange(cu.Refs);
            foreach (var k in cu.Wildcards.Keys) wild.Add(k);
        }
        else
        {
            var byName = cu.Procs.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.ToList());
            var seen = new HashSet<Proc>();
            var work = new Queue<Proc>();
            void Enq(Proc p) { if (seen.Add(p)) work.Enqueue(p); }
            Enq(test);
            foreach (var h in test.Handlers) if (byName.TryGetValue(h, out var hs)) hs.ForEach(Enq);
            foreach (var p in cu.Procs) if (p.IsTrigger || p.IsSubscriber) Enq(p);
            var usedGlobals = new HashSet<string>();
            while (work.Count > 0)
            {
                var p = work.Dequeue();
                refs.AddRange(p.Refs);
                foreach (var k in p.Wildcards.Keys) wild.Add(k);
                foreach (var id in p.Identifiers)
                {
                    if (byName.TryGetValue(id, out var ps)) ps.ForEach(Enq);
                    if (cu.GlobalVarTypes.TryGetValue(id, out var gv) && usedGlobals.Add(id)) refs.AddRange(gv);
                }
            }
        }
        var roots = ResolveRefs(refs);
        wildKinds = wild.Where(k => !_opt.DisabledWildcards.Contains(k)).ToList();
        foreach (var k in wildKinds) roots.Add(WildNode(k));
        roots.AddRange(GlobalRoots);
        return roots;
    }
}
