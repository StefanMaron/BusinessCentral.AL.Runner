using System.Diagnostics;
using System.Text.Json;

namespace CallGraphSpike;

/// <summary>`proc-project` / `proc-buckets`: the same questions as `project` / `buckets`, at procedure granularity.</summary>
static class ProcCli
{
    static bool Has(ulong[] b, int i) => (b[i >> 6] & (1UL << (i & 63))) != 0;

    public static int Project(List<(string, string)> apps, HashSet<string> targets, string outPath, GraphOptions opt)
    {
        var sw = Stopwatch.StartNew();
        var objs = apps.SelectMany(a => ProcExtract.ParseApp(a.Item1, a.Item2)).ToList();
        var parseSecs = sw.Elapsed.TotalSeconds; sw.Restart();
        var g = new ProcGraph(objs, opt, o => o.IsTestCodeunit && targets.Contains(o.App));
        var bits = g.TestBitsPerNode(out _);
        var graphSecs = sw.Elapsed.TotalSeconds;
        var testNames = g.TestNodes.Select(n => $"Codeunit{objs[g.Nodes[n].Obj].Id}.{g.Nodes[n].Proc!.DisplayName}").ToList();
        var reach = testNames.ToDictionary(t => t, _ => new List<string>());
        var procs = new List<object>();
        var tgtObjs = Enumerable.Range(0, objs.Count).Where(i => targets.Contains(objs[i].App)).ToList();
        foreach (var i in tgtObjs)
        {
            var union = new ulong[bits[0].Length];
            foreach (var n in g.RealNodesOf(i))
            {
                var b = bits[n];
                for (var k = 0; k < union.Length; k++) union[k] |= b[k];
                procs.Add(new { key = objs[i].Key, proc = g.Nodes[n].Proc!.DisplayName, trigger = g.Nodes[n].Proc!.IsTrigger, tests = ProcGraph.Pop(b) , testIdx = Enumerable.Range(0, testNames.Count).Where(t => Has(b, t)).ToList() });
            }
            for (var t = 0; t < testNames.Count; t++) if (Has(union, t)) reach[testNames[t]].Add(objs[i].Key);
        }
        var wild = ProcGraph.WildKinds.Select((k, w) => (k, w)).ToDictionary(x => x.k, x => ProcGraph.Pop(bits[g.WildNode[x.w]]));
        var result = new
        {
            granularity = "procedure",
            options = opt,
            timings = new { parseSecs, graphSecs },
            stats = new { nodes = g.Nodes.Count, g.LargestScc, g.SccCount, counters = g.Counters, testsReachingWildcard = wild, testsReachingPlatformEvents = ProcGraph.Pop(bits[g.GlobalRoot]) },
            objects = tgtObjs.Select(i => objs[i]).Select(o => new { key = o.Key, o.Kind, o.Id, name = o.DisplayName, o.App, file = o.File, start = o.StartLine, end = o.EndLine }),
            testNames,
            tests = reach.ToDictionary(kv => kv.Key, kv => new { reach = kv.Value, total = 0, directWildcards = Array.Empty<string>(), wildcardsReached = Array.Empty<string>() }),
            procs,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields = true }));
        Console.WriteLine($"objects={objs.Count} nodes={g.Nodes.Count} tests={testNames.Count} largestScc={g.LargestScc} parse={parseSecs:F1}s graph+reach={graphSecs:F1}s");
        return 0;
    }

    public static int Buckets(List<(string, string)> apps, List<(string, string)> buckets, string outPath, GraphOptions opt)
    {
        var sw = Stopwatch.StartNew();
        var shared = apps.SelectMany(a => ProcExtract.ParseApp(a.Item1, a.Item2)).ToList();
        var bucketObjs = buckets.Select(b => (b.Item1, ProcExtract.ParseApp(b.Item1, b.Item2))).ToList();
        var parseSecs = sw.Elapsed.TotalSeconds;
        double graphSecs = 0;
        long totalTests = 0;
        var objCounts = new Dictionary<string, long>();
        var procCounts = new Dictionary<string, long>();   // "objKey|procName" (shared apps only)
        var wildTests = ProcGraph.WildKinds.ToDictionary(k => k, _ => 0L);
        long platformTests = 0;
        object? firstStats = null;
        var perBucket = new Dictionary<string, object>();
        foreach (var (bname, bobjs) in bucketObjs)
        {
            sw.Restart();
            var objs = shared.Concat(bobjs).ToList();
            var g = new ProcGraph(objs, opt, o => o.IsTestCodeunit && o.App == bname);
            var bits = g.TestBitsPerNode(out var words);
            graphSecs += sw.Elapsed.TotalSeconds;
            firstStats ??= new { nodes = g.Nodes.Count, g.LargestScc, g.SccCount, counters = g.Counters };
            for (var i = 0; i < shared.Count; i++)
            {
                var union = new ulong[words];
                var j = 0;
                foreach (var n in g.RealNodesOf(i))
                {
                    var b = bits[n];
                    for (var k = 0; k < words; k++) union[k] |= b[k];
                    if (objs[i].App == "Base Application")
                    {
                        var pk = $"{objs[i].Key}|{g.Nodes[n].Proc!.Name}|{j}";
                        procCounts[pk] = procCounts.GetValueOrDefault(pk) + ProcGraph.Pop(b);
                    }
                    j++;
                }
                var c = ProcGraph.Pop(union);
                if (c > 0) objCounts[objs[i].Key] = objCounts.GetValueOrDefault(objs[i].Key) + c;
            }
            for (var w = 0; w < ProcGraph.WildKinds.Length; w++) wildTests[ProcGraph.WildKinds[w]] += ProcGraph.Pop(bits[g.WildNode[w]]);
            platformTests += ProcGraph.Pop(bits[g.GlobalRoot]);
            totalTests += g.TestNodes.Count;
            perBucket[bname] = new { tests = g.TestNodes.Count, largestScc = g.LargestScc, secs = sw.Elapsed.TotalSeconds };
            Console.WriteLine($"{bname}: tests={g.TestNodes.Count} nodes={g.Nodes.Count} largestScc={g.LargestScc} {sw.Elapsed.TotalSeconds:F1}s");
        }
        var result = new
        {
            granularity = "procedure",
            options = opt,
            timings = new { parseSecs, graphSecs },
            totalTests, perBucket, stats = firstStats,
            wildcardTestsReaching = wildTests, platformEventTests = platformTests,
            objects = shared.Select(o => new { key = o.Key, o.Kind, o.Id, name = o.DisplayName, o.App, tests = objCounts.GetValueOrDefault(o.Key) }),
            procCounts,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields = true }));
        Console.WriteLine($"totalTests={totalTests} parse={parseSecs:F1}s graph+reach={graphSecs:F1}s");
        return 0;
    }
}
