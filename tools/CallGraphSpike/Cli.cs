using System.Diagnostics;
using System.Text.Json;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace CallGraphSpike;

/// <summary>
/// Usage:
///   CallGraphSpike dump &lt;file.al&gt;
///   CallGraphSpike project --app Name=dir ... --targets Name,Name --out result.json [flags]
///       per test method of every Subtype=Test codeunit: the objects of the target apps it can reach
///   CallGraphSpike buckets --app Name=dir ... --bucket Name=dir ... --out result.json [flags]
///       one graph per bucket (shared apps + that bucket); per object, how many tests reach it
/// flags: --no-wildcards all|Kind,Kind   --no-global-roots   --no-tablerelation   --object-level-tests
///        --drop-edges via,via (ablation; names as printed in stats.EdgeCounts)
/// </summary>
static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 2 && args[0] == "dump") return Dump(args[1]);
        if (args.Length < 1) { Console.Error.WriteLine("usage: see Cli.cs"); return 2; }
        var apps = new List<(string, string)>();
        var buckets = new List<(string, string)>();
        var targets = new HashSet<string>();
        string? outPath = null;
        var opt = new GraphOptions();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app": apps.Add(Split(args[++i])); break;
                case "--bucket": buckets.Add(Split(args[++i])); break;
                case "--targets": foreach (var t in args[++i].Split(',')) targets.Add(t); break;
                case "--out": outPath = args[++i]; break;
                case "--no-wildcards":
                    var v = args[++i];
                    foreach (var k in v == "all" ? Graph.WildKinds : v.Split(',')) opt.DisabledWildcards.Add(k);
                    break;
                case "--no-global-roots": opt.GlobalRoots = false; break;
                case "--no-tablerelation": opt.TableRelation = false; break;
                case "--object-level-tests": opt.ProcedureLevelTests = false; break;
                case "--drop-edges": foreach (var e in args[++i].Split(',')) opt.DroppedEdges.Add(e); break;
                default: Console.Error.WriteLine("unknown arg " + args[i]); return 2;
            }
        }
        if (outPath == null) { Console.Error.WriteLine("--out required"); return 2; }
        return args[0] switch
        {
            "project" => Project(apps, targets, outPath, opt),
            "buckets" => Buckets(apps, buckets, outPath, opt),
            _ => 2,
        };
    }

    static (string, string) Split(string s) { var i = s.IndexOf('='); return (s[..i], s[(i + 1)..]); }

    static List<AlObject> ParseAll(List<(string, string)> apps, Extract.Stats stats)
    {
        var all = new List<AlObject>();
        foreach (var (name, dir) in apps) all.AddRange(Extract.ParseApp(name, dir, stats));
        return all;
    }

    static int Project(List<(string, string)> apps, HashSet<string> targets, string outPath, GraphOptions opt)
    {
        var sw = Stopwatch.StartNew();
        var stats = new Extract.Stats();
        var objs = ParseAll(apps, stats);
        var parseSecs = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var g = new Graph(objs, opt);
        var graphSecs = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var tests = new Dictionary<string, object>();
        var targetIdx = Enumerable.Range(0, g.N).Where(i => targets.Contains(objs[i].App)).ToList();
        foreach (var cu in objs.Where(o => o.IsTestCodeunit && targets.Contains(o.App)))
            foreach (var p in cu.Procs.Where(p => p.IsTest))
            {
                var roots = g.TestRoots(cu, p, out var wild);
                var bits = g.Closure(roots);
                var wildReached = Graph.WildKinds.Where(k => Graph.Has(bits, g.WildNode(k))).ToList();
                tests[$"Codeunit{cu.Id}.{p.DisplayName}"] = new
                {
                    reach = targetIdx.Where(i => Graph.Has(bits, i)).Select(i => objs[i].Key).ToList(),
                    total = Graph.Count(bits),
                    directWildcards = wild,
                    wildcardsReached = wildReached,
                };
            }
        var testSecs = sw.Elapsed.TotalSeconds;
        var result = new
        {
            options = opt,
            timings = new { parseSecs, graphSecs, testSecs },
            stats = new
            {
                stats.Files, stats.Objects, nodes = g.Total, g.SccCount, g.LargestScc,
                globalRoots = g.GlobalRoots.Count,
                globalRootClosure = g.GlobalRoots.Count == 0 ? 0 : Graph.Count(g.Closure(g.GlobalRoots)),
                g.UnresolvedPublishers, g.EdgeCounts, g.Unresolved, anyKindParents = stats.AnyKindParents,
                objectsByApp = objs.GroupBy(o => o.App).ToDictionary(x => x.Key, x => x.Count()),
            },
            objects = targetIdx.Select(i => objs[i]).Select(o => new { key = o.Key, o.Kind, o.Id, name = o.DisplayName, o.App, file = o.File, start = o.StartLine, end = o.EndLine }),
            tests,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields = true }));
        Console.WriteLine($"objects={objs.Count} tests={tests.Count} parse={parseSecs:F1}s graph={graphSecs:F1}s tests={testSecs:F1}s");
        return 0;
    }

    static int Buckets(List<(string, string)> apps, List<(string, string)> buckets, string outPath, GraphOptions opt)
    {
        var sw = Stopwatch.StartNew();
        var stats = new Extract.Stats();
        var shared = ParseAll(apps, stats);
        var bucketObjs = buckets.Select(b => (b.Item1, Extract.ParseApp(b.Item1, b.Item2, stats))).ToList();
        var parseSecs = sw.Elapsed.TotalSeconds;
        var counts = new Dictionary<string, long>();          // object key -> tests reaching it
        var perBucket = new Dictionary<string, object>();
        var wildTests = Graph.WildKinds.ToDictionary(k => k, _ => 0L);
        var wildDirect = Graph.WildKinds.ToDictionary(k => k, _ => 0L);
        var reachSizes = new List<int>();
        long totalTests = 0;
        double graphSecs = 0, testSecs = 0;
        object? firstStats = null;
        foreach (var (bname, bobjs) in bucketObjs)
        {
            sw.Restart();
            var objs = shared.Concat(bobjs).ToList();
            var g = new Graph(objs, opt);
            graphSecs += sw.Elapsed.TotalSeconds;
            sw.Restart();
            firstStats ??= new
            {
                nodes = g.Total, g.SccCount, g.LargestScc, globalRoots = g.GlobalRoots.Count,
                globalRootClosure = g.GlobalRoots.Count == 0 ? 0 : Graph.Count(g.Closure(g.GlobalRoots)),
                g.UnresolvedPublishers, g.EdgeCounts, g.Unresolved,
                wildcardClosure = Graph.WildKinds.ToDictionary(k => k, k => Graph.Count(g.ReachOf(g.WildNode(k)))),
            };
            var local = new long[g.Total];
            var n = 0;
            foreach (var cu in bobjs.Where(o => o.IsTestCodeunit))
            {
                foreach (var p in cu.Procs.Where(p => p.IsTest))
                {
                    var roots = g.TestRoots(cu, p, out var wild);
                    var bits = g.Closure(roots);
                    foreach (var k in wild) wildDirect[k]++;
                    foreach (var k in Graph.WildKinds) if (Graph.Has(bits, g.WildNode(k))) wildTests[k]++;
                    reachSizes.Add(Graph.Count(bits));
                    for (var w = 0; w < bits.Length; w++)
                    {
                        var word = bits[w];
                        while (word != 0)
                        {
                            var b = System.Numerics.BitOperations.TrailingZeroCount(word);
                            local[(w << 6) + b]++;
                            word &= word - 1;
                        }
                    }
                    n++;
                }
            }
            for (var i = 0; i < g.N; i++)
                if (local[i] > 0 && objs[i].App != bname)
                    counts[objs[i].Key] = counts.GetValueOrDefault(objs[i].Key) + local[i];
            totalTests += n;
            perBucket[bname] = new { tests = n, objects = bobjs.Count };
            testSecs += sw.Elapsed.TotalSeconds;
            Console.WriteLine($"{bname}: tests={n}");
        }
        var result = new
        {
            options = opt,
            timings = new { parseSecs, graphSecs, testSecs },
            totalTests,
            perBucket,
            stats = firstStats,
            wildcardTestsReaching = wildTests,
            wildcardTestsDirect = wildDirect,
            reachSizes,
            objects = shared.Select(o => new { key = o.Key, o.Kind, o.Id, name = o.DisplayName, o.App, tests = counts.GetValueOrDefault(o.Key) }),
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields = true }));
        Console.WriteLine($"totalTests={totalTests} parse={parseSecs:F1}s graph={graphSecs:F1}s tests={testSecs:F1}s");
        return 0;
    }

    static int Dump(string path)
    {
        var src = File.ReadAllText(path);
        var opts = new NavCA.ParseOptions(runtimeVersion: null!, preprocessorSymbols: Array.Empty<string>(), documentationMode: NavCA.DocumentationMode.None);
        var tree = NavSyntax.SyntaxTree.ParseObjectText(src, path: path, encoding: null!, opts, default);
        void D(NavCA.SyntaxNode n, int d)
        {
            var t = n.ToString().Replace("\n", " ").Replace("\r", "");
            if (t.Length > 80) t = t[..80];
            Console.WriteLine($"{new string(' ', d * 2)}{n.Kind} [{n.GetType().Name}] {t}");
            foreach (var c in n.ChildNodes()) D(c, d + 1);
        }
        D(tree.GetRoot(), 0);
        return 0;
    }
}
