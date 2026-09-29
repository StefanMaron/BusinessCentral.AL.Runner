using System.Numerics;

namespace CallGraphSpike;

/// <summary>
/// Procedure-granularity reachability. Nodes: every procedure and trigger declaration; per object a
/// "#run" node (running the object: a page's/report's/xmlport's/query's triggers, a codeunit's OnRun)
/// and, for tables, a "#records" node (a trigger-running record operation: that table's triggers,
/// its extensions' triggers, and subscribers to its trigger events); one wildcard node per kind for
/// non-constant dynamic dispatch; one node for subscribers to platform-raised events.
/// Calls are resolved by declared variable type and member name, every overload and extension
/// included; a call whose receiver type cannot be determined falls back to every procedure of that
/// name in every object (and, for a trigger-running record method, to every table's triggers).
/// </summary>
sealed class ProcGraph
{
    public static readonly string[] WildKinds = { "Table", "Page", "Codeunit", "Report", "Query", "XmlPort" };
    static readonly HashSet<string> TriggerBuiltins = new() { "insert", "modify", "delete", "rename", "validate", "modifyall", "deleteall" };

    public readonly List<PObj> Objs;
    public readonly List<(int Obj, PProc? Proc, string Label)> Nodes = new();
    readonly List<List<int>> _adj = new();
    readonly int[] _run, _records;
    readonly List<int>[] _procNodes;
    public readonly int[] WildNode = new int[WildKinds.Length];
    public readonly int GlobalRoot, AnyReportRun, AnyRecordOp;
    readonly Dictionary<(string, string), List<int>> _byName = new();
    readonly Dictionary<(string, int), List<int>> _byId = new();
    readonly Dictionary<(string, string), List<int>> _extOf = new();
    readonly Dictionary<string, List<int>> _implOf = new();
    readonly Dictionary<string, List<int>> _procsAnywhere = new();
    readonly GraphOptions _opt;
    public readonly Dictionary<string, long> Counters = new();
    public readonly List<int> TestNodes = new();
    readonly Dictionary<int, List<(string Event, string Element, int Sub)>> _triggerEventSubs = new();

    void Count(string k, long n = 1) => Counters[k] = Counters.GetValueOrDefault(k) + n;
    int NewNode(int obj, PProc? p, string label) { Nodes.Add((obj, p, label)); _adj.Add(new List<int>()); return Nodes.Count - 1; }
    void Edge(int a, int b, string via) { if (a == b || _opt.DroppedEdges.Contains(via)) return; _adj[a].Add(b); Count("edge:" + via); }

    public ProcGraph(List<PObj> objs, GraphOptions opt, Func<PObj, bool> isTestScope)
    {
        Objs = objs; _opt = opt;
        _run = new int[objs.Count]; _records = new int[objs.Count]; _procNodes = new List<int>[objs.Count];
        for (var i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            Add(_byName, (o.Kind, o.Name), i);
            if (o.Id != 0) Add(_byId, (o.Kind, o.Id), i);
            if (o.ExtendsName != null) Add(_extOf, (o.Kind[..^"Extension".Length], o.ExtendsName), i);
            foreach (var itf in o.Implements) Add(_implOf, itf, i);
            _run[i] = NewNode(i, null, "#run");
            _records[i] = o.Kind is "Table" or "TableExtension" ? NewNode(i, null, "#records") : -1;
            _procNodes[i] = new List<int>();
            foreach (var p in o.Procs)
            {
                var n = NewNode(i, p, p.Name);
                _procNodes[i].Add(n);
                if (!p.IsTrigger) Add(_procsAnywhere, p.Name, n);
            }
        }
        for (var w = 0; w < WildKinds.Length; w++) WildNode[w] = NewNode(-1, null, "*" + WildKinds[w]);
        DeadEnd = NewNode(-1, null, "#disabled-wildcard");
        GlobalRoot = NewNode(-1, null, "#platform-events");
        AnyReportRun = NewNode(-1, null, "#platform:any-report-run");
        AnyRecordOp = NewNode(-1, null, "#platform:any-record-op");
        // subscriptions first: precise record operations look up trigger-event subscribers
        for (var i = 0; i < objs.Count; i++)
            foreach (var n in _procNodes[i])
                if (Nodes[n].Proc!.Subscription is { } sub) WireSubscriber(n, sub);
        for (var i = 0; i < objs.Count; i++) BuildObject(i, isTestScope(objs[i]));
        for (var w = 0; w < WildKinds.Length; w++)
            for (var i = 0; i < objs.Count; i++)
                if (objs[i].Kind == WildKinds[w]) Edge(WildNode[w], WildKinds[w] == "Table" ? _records[i] : _run[i], "wildcard->all");
        // platform events raised by an operation rather than by the session: every report run raises
        // "Reporting Triggers" events, every record operation "Global Triggers" events
        for (var i = 0; i < objs.Count; i++)
        {
            if (objs[i].Kind == "Report") Edge(_run[i], AnyReportRun, "report-run->platform-reporting-events");
            if (objs[i].Kind == "Table") Edge(_records[i], AnyRecordOp, "record-op->platform-global-triggers");
        }
    }

    static void Add<TK>(Dictionary<TK, List<int>> d, TK k, int v) where TK : notnull
    { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<int>(); l.Add(v); }

    IEnumerable<int> Resolve(string kind, string name, int id = 0)
    {
        if (id != 0) return _byId.TryGetValue((kind, id), out var l) ? l : Enumerable.Empty<int>();
        return _byName.TryGetValue((kind, name), out var n) ? n : Enumerable.Empty<int>();
    }
    IEnumerable<int> Exts(int obj) => _extOf.TryGetValue((Objs[obj].Kind, Objs[obj].Name), out var l) ? l : Enumerable.Empty<int>();
    IEnumerable<int> WithExts(int obj) => new[] { obj }.Concat(Exts(obj));
    IEnumerable<int> ProcsNamed(int obj, string name) => _procNodes[obj].Where(n => !Nodes[n].Proc!.IsTrigger && Nodes[n].Proc!.Name == name);
    // a disabled wildcard kind (ablation) points at a node nothing leads out of
    int Wild(string kind) => _opt.DisabledWildcards.Contains(kind) ? DeadEnd : WildNode[Array.IndexOf(WildKinds, kind)];
    public readonly int DeadEnd;

    int? BaseOf(int obj)
    {
        var o = Objs[obj];
        if (o.ExtendsName == null) return null;
        var l = Resolve(o.Kind[..^"Extension".Length], o.ExtendsName).ToList();
        return l.Count > 0 ? l[0] : null;
    }

    /// <summary>The table `Rec` denotes inside object `obj`, if any.</summary>
    IEnumerable<int> RecTable(int obj)
    {
        var o = Objs[obj];
        switch (o.Kind)
        {
            case "Table": return new[] { obj };
            case "TableExtension": return BaseOf(obj) is int b ? new[] { b } : Array.Empty<int>();
            case "Page": return o.SourceTable == null ? Array.Empty<int>() : Resolve("Table", o.SourceTable);
            case "PageExtension": return BaseOf(obj) is int bp && Objs[bp].SourceTable != null ? Resolve("Table", Objs[bp].SourceTable!) : Array.Empty<int>();
            case "Codeunit": return o.TableNo == null ? Array.Empty<int>() : Resolve("Table", o.TableNo);
        }
        return Array.Empty<int>();
    }

    void BuildObject(int i, bool testScope)
    {
        var o = Objs[i];
        var run = _run[i];
        // running / record-operating an object reaches its triggers and its extensions'
        foreach (var n in _procNodes[i])
            if (Nodes[n].Proc!.IsTrigger) Edge(o.Kind is "Table" or "TableExtension" ? _records[i] : run, n, "object->trigger");
        foreach (var e in Exts(i))
        {
            Edge(run, _run[e], "base->extension");
            if (_records[i] >= 0 && _records[e] >= 0) Edge(_records[i], _records[e], "base->extension");
        }
        if (o.Kind is "Page" or "PageExtension")
            foreach (var t in RecTable(i)) Edge(run, _records[t], "page->sourcetable");
        foreach (var part in o.PartPages) foreach (var t in Resolve("Page", part)) Edge(run, _run[t], "page->part");
        foreach (var (k, n) in o.RunObjects) foreach (var t in Resolve(k, n)) Edge(run, _run[t], "action->runobject");
        if (o.Kind == "Table")
            foreach (var pg in new[] { o.LookupPage, o.DrillDownPage })
                if (pg != null) foreach (var t in Resolve("Page", pg)) Edge(_records[i], _run[t], "table->lookuppage");

        foreach (var n in _procNodes[i])
        {
            var p = Nodes[n].Proc!;
            foreach (var c in p.Calls) WireCall(i, n, p, c);
            if (testScope && p.IsTest)
            {
                TestNodes.Add(n);
                foreach (var h in p.Handlers) foreach (var hn in _procNodes[i].Where(x => Nodes[x].Proc!.Name == h)) Edge(n, hn, "test->handler");
                foreach (var x in _procNodes[i]) if (Nodes[x].Proc!.IsTrigger || Nodes[x].Proc!.IsSubscriber) Edge(n, x, "test->codeunit-trigger/subscriber");
                if (_opt.GlobalRoots) Edge(n, GlobalRoot, "test->platform-events");
            }
        }
    }

    void WireSubscriber(int subNode, (string Kind, string Pub, int PubId, string Event) sub)
    {
        var kinds = sub.Kind == "*" ? WildKinds : new[] { sub.Kind };
        var pubs = kinds.SelectMany(k => Resolve(k, sub.Pub, sub.PubId)).ToList();
        if (pubs.Count == 0) { Count("subscriber:publisher-unresolved"); Edge(GlobalRoot, subNode, "platform->subscriber"); return; }
        foreach (var p in pubs)
        {
            if (Objs[p].App == "System")
            {
                switch (Objs[p].Name)
                {
                    case "reporting triggers": Edge(AnyReportRun, subNode, "platform-reporting->subscriber"); break;
                    case "global triggers": Edge(AnyRecordOp, subNode, "platform-global->subscriber"); break;
                    default: Edge(GlobalRoot, subNode, "platform->subscriber"); break;
                }
                continue;
            }
            var pubProcs = WithExts(p).SelectMany(x => ProcsNamed(x, sub.Event)).ToList();
            if (pubProcs.Count > 0) { foreach (var pp in pubProcs) Edge(pp, subNode, "publisher->subscriber"); continue; }
            if (sub.Event.EndsWith("event"))
            {
                Edge(Objs[p].Kind == "Table" ? _records[p] : _run[p], subNode, "trigger-event->subscriber");
                if (Objs[p].Kind == "Table")
                {
                    if (!_triggerEventSubs.TryGetValue(p, out var l)) _triggerEventSubs[p] = l = new();
                    l.Add((sub.Event, Nodes[subNode].Proc!.SubscriptionElement, subNode));
                }
                continue;
            }
            Count("subscriber:event-unresolved");
            Edge(Objs[p].Kind == "Table" ? _records[p] : _run[p], subNode, "unresolved-event->subscriber");
        }
    }

    /// <summary>A trigger-running record operation on table `t`.</summary>
    void RecordOp(int from, int t, string member, string? field)
    {
        if (!_opt.PreciseRecordOps || _records[t] < 0) { if (_records[t] >= 0) Edge(from, _records[t], "record-op->triggers"); return; }
        var op = member switch { "modifyall" => "modify", "deleteall" => "delete", _ => member };
        foreach (var x in WithExts(t))
            foreach (var n in _procNodes[x])
            {
                var pr = Nodes[n].Proc!;
                if (!pr.IsTrigger) continue;
                var disp = pr.DisplayName.ToLowerInvariant();
                var cut = disp.LastIndexOf(" - ", StringComparison.Ordinal);
                var container = cut < 0 ? null : Extract.Norm(disp[..cut]);
                var trig = cut < 0 ? disp : disp[(cut + 3)..];
                bool hit = op == "validate"
                    ? container != null && trig.Contains("validate") && (field == null || container == field)
                    : container == null && trig.Contains(op);
                if (hit) Edge(from, n, "record-op->trigger(precise)");
            }
        if (_triggerEventSubs.TryGetValue(t, out var subs))
            foreach (var (ev, el, sn) in subs)
                if (ev.Contains(op) && (op != "validate" || field == null || el == field)) Edge(from, sn, "record-op->trigger-event-subscriber(precise)");
        Edge(from, AnyRecordOp, "record-op->platform-global-triggers");
    }

    TypeRef? TypeOfName(int obj, PProc p, string name)
    {
        if (p.Locals.TryGetValue(name, out var t)) return t;
        if (Objs[obj].Globals.TryGetValue(name, out t)) return t;
        if (BaseOf(obj) is int b && Objs[b].Globals.TryGetValue(name, out t)) return t; // report/page extensions see base dataitems/parts
        return null;
    }

    void ToType(int from, TypeRef t, string member, string via, string? field = null)
    {
        if (t.Kind == "RecordRef") { if (TriggerBuiltins.Contains(member)) { Edge(from, Wild("Table"), "wildcard:Table"); Count("site:recordref-trigger-op"); } return; }
        if (t.Kind == "FieldRef") { if (member == "validate") { Edge(from, Wild("Table"), "wildcard:Table"); Count("site:fieldref-validate"); } return; }
        if (t.Kind == "Interface")
        {
            if (_implOf.TryGetValue(t.Name, out var impls))
                foreach (var im in impls) foreach (var x in ProcsNamed(im, member)) Edge(from, x, "interface->implementation");
            return;
        }
        foreach (var target in Resolve(t.Kind, t.Name))
        {
            foreach (var x in WithExts(target)) foreach (var pn in ProcsNamed(x, member)) Edge(from, pn, via);
            switch (t.Kind)
            {
                case "Table": if (TriggerBuiltins.Contains(member)) RecordOp(from, target, member, field); break;
                case "Codeunit": if (member == "run") Edge(from, _run[target], "codeunit.run"); break;
                default: Edge(from, _run[target], "run-object"); break; // pages/reports/xmlports/queries: any use may run it
            }
        }
    }

    TypeRef? ReturnTypeOf(int obj, PProc p, string? target, string fn)
    {
        IEnumerable<int> objs;
        if (target == null) objs = WithExts(obj).Concat(BaseOf(obj) is int b ? new[] { b } : Array.Empty<int>());
        else if (TypeOfName(obj, p, target) is { } tt && tt.Kind is not ("RecordRef" or "FieldRef" or "Interface"))
            objs = Resolve(tt.Kind, tt.Name).SelectMany(WithExts);
        else return null;
        foreach (var x in objs)
            foreach (var pn in ProcsNamed(x, fn))
                if (Nodes[pn].Proc!.ReturnType is { } rt) return rt;
        return null;
    }

    void Fallback(int from, CallSite c, string why)
    {
        Count("fallback:" + why);
        if (_procsAnywhere.TryGetValue(c.Member, out var all)) foreach (var x in all) Edge(from, x, "name-fallback");
        if (TriggerBuiltins.Contains(c.Member)) { Edge(from, Wild("Table"), "wildcard:Table"); Count("site:unknown-receiver-trigger-op"); }
    }

    void WireCall(int obj, int from, PProc p, CallSite c)
    {
        foreach (var (k, n, id) in c.RunTargets)
            foreach (var t in Resolve(k, n, id)) Edge(from, k == "Table" ? _records[t] : _run[t], "run-literal");
        foreach (var k in c.RunWildcards) { Edge(from, Wild(k), "wildcard:" + k); Count("site:dynamic-" + k); }
        if (c.LookupPageOfVar != null)
        {
            if (TypeOfName(obj, p, c.LookupPageOfVar) is { Kind: "Table" } tt)
                foreach (var t in Resolve("Table", tt.Name))
                    foreach (var pg in new[] { Objs[t].LookupPage, Objs[t].DrillDownPage })
                        if (pg != null) foreach (var x in Resolve("Page", pg)) Edge(from, _run[x], "page.run(0)->lookuppage");
                        else { }
            else { Edge(from, Wild("Page"), "wildcard:Page"); Count("site:dynamic-Page"); }
        }
        if (c.RunTargets.Count > 0 || c.RunWildcards.Count > 0 || c.LookupPageOfVar != null) return;

        if (c.Receiver == null && c.ChainHead == null && c.ReceiverCall == null)
        {
            // unqualified: own object (and base object for an extension), then Rec's table
            var own = WithExts(obj).Concat(BaseOf(obj) is int b ? WithExts(b) : Array.Empty<int>()).Distinct().SelectMany(x => ProcsNamed(x, c.Member)).ToList();
            foreach (var x in own) Edge(from, x, "call:own");
            if (own.Count > 0) return;
            foreach (var t in RecTable(obj))
            {
                foreach (var x in WithExts(t)) foreach (var pn in ProcsNamed(x, c.Member)) Edge(from, pn, "call:rec");
                if (TriggerBuiltins.Contains(c.Member)) RecordOp(from, t, c.Member, c.FieldArg);
            }
            return;
        }
        if (c.Receiver != null)
        {
            switch (c.Receiver)
            {
                case "rec" or "xrec":
                    foreach (var t in RecTable(obj)) ToType(from, new TypeRef("Table", Objs[t].Name), c.Member, "call:rec", c.FieldArg);
                    return;
                case "currpage":
                    foreach (var x in WithExts(obj).Concat(BaseOf(obj) is int b ? WithExts(b) : Array.Empty<int>()).Distinct())
                    { foreach (var pn in ProcsNamed(x, c.Member)) Edge(from, pn, "call:currpage"); Edge(from, _run[x], "currpage"); }
                    return;
                case "currreport":
                    Edge(from, _run[obj], "currreport");
                    if (BaseOf(obj) is int br) Edge(from, _run[br], "currreport");
                    return;
                case "this":
                    foreach (var pn in WithExts(obj).SelectMany(x => ProcsNamed(x, c.Member))) Edge(from, pn, "call:own");
                    return;
            }
            var recvType = TypeOfName(obj, p, c.Receiver);
            if (recvType != null) { ToType(from, recvType.Value, c.Member, "call:typed", c.FieldArg); return; }
            Count("skip:untyped-receiver"); // Text/Json/Dialog/... or a Rec field: no AL code behind it
            return;
        }
        if (c.ReceiverCall != null)
        {
            var rt = ReturnTypeOf(obj, p, c.ReceiverCallTarget, c.ReceiverCall);
            if (rt != null) { ToType(from, rt.Value, c.Member, "call:via-return"); return; }
            Fallback(from, c, "receiver-call");
            return;
        }
        // chained receiver a.b.M(): a page/testpage/report head runs that object (parts included)
        if (c.ChainHead == "currpage")
        {
            Edge(from, _run[obj], "currpage");
            foreach (var part in Objs[obj].PartPages) foreach (var x in Resolve("Page", part)) { Edge(from, _run[x], "currpage-part"); foreach (var pn in WithExts(x).SelectMany(y => ProcsNamed(y, c.Member))) Edge(from, pn, "call:part"); }
            if (BaseOf(obj) is int bp) foreach (var part in Objs[bp].PartPages) foreach (var x in Resolve("Page", part)) { Edge(from, _run[x], "currpage-part"); foreach (var pn in WithExts(x).SelectMany(y => ProcsNamed(y, c.Member))) Edge(from, pn, "call:part"); }
            return;
        }
        if (c.ChainHead != null && TypeOfName(obj, p, c.ChainHead) is { } ht)
        {
            if (ht.Kind is "Page" or "Report") { foreach (var x in Resolve(ht.Kind, ht.Name)) Edge(from, _run[x], "run-object"); return; }
            if (ht.Kind == "Table") { Count("skip:record-field-chain"); return; } // Rec."Field".Method(): a built-in on a field value
        }
        Fallback(from, c, "chain");
    }

    // ---- reachability: test bitsets propagated forward over SCCs in topological order ----
    public ulong[][] TestBitsPerNode(out int words)
    {
        var n = Nodes.Count;
        var testIndex = new Dictionary<int, int>();
        for (var i = 0; i < TestNodes.Count; i++) testIndex[TestNodes[i]] = i;
        words = Math.Max(1, (TestNodes.Count + 63) / 64);
        var (comp, sccs) = Scc();
        var bits = new ulong[sccs.Count][];
        // Tarjan emits sinks first; walk in reverse emission order = topological order
        for (var c = sccs.Count - 1; c >= 0; c--)
        {
            bits[c] ??= new ulong[words];
            var b = bits[c];
            foreach (var v in sccs[c]) if (testIndex.TryGetValue(v, out var ti)) b[ti >> 6] |= 1UL << (ti & 63);
            foreach (var v in sccs[c])
                foreach (var w in _adj[v])
                {
                    var cw = comp[w];
                    if (cw == c) continue;
                    var tb = bits[cw] ??= new ulong[words];
                    for (var k = 0; k < words; k++) tb[k] |= b[k];
                }
        }
        var res = new ulong[n][];
        for (var v = 0; v < n; v++) res[v] = bits[comp[v]];
        LargestScc = sccs.Max(s => s.Count); SccCount = sccs.Count;
        return res;
    }
    public int LargestScc, SccCount;

    (int[] comp, List<List<int>> sccs) Scc()
    {
        var n = Nodes.Count;
        var index = new int[n]; Array.Fill(index, -1);
        var low = new int[n]; var onStack = new bool[n]; var comp = new int[n];
        var stack = new Stack<int>(); var sccs = new List<List<int>>(); var idx = 0;
        var call = new Stack<(int v, int ei)>();
        for (var s = 0; s < n; s++)
        {
            if (index[s] != -1) continue;
            call.Push((s, 0)); index[s] = low[s] = idx++; stack.Push(s); onStack[s] = true;
            while (call.Count > 0)
            {
                var (v, ei) = call.Pop();
                if (ei < _adj[v].Count)
                {
                    call.Push((v, ei + 1));
                    var w = _adj[v][ei];
                    if (index[w] == -1) { index[w] = low[w] = idx++; stack.Push(w); onStack[w] = true; call.Push((w, 0)); }
                    else if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                }
                else
                {
                    if (low[v] == index[v])
                    {
                        var c = new List<int>(); int w;
                        do { w = stack.Pop(); onStack[w] = false; comp[w] = sccs.Count; c.Add(w); } while (w != v);
                        sccs.Add(c);
                    }
                    if (call.Count > 0) { var (u, _) = call.Peek(); low[u] = Math.Min(low[u], low[v]); }
                }
            }
        }
        return (comp, sccs);
    }

    public IEnumerable<int> RealNodesOf(int obj) => _procNodes[obj];
    public static int Pop(ulong[] b) { var c = 0; foreach (var w in b) c += BitOperations.PopCount(w); return c; }
}
