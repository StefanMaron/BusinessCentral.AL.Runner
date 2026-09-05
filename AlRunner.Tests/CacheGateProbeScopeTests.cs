// CacheGateProbeScopeTests — issue #2557.
//
// Two whole-tree scans used to run on every bundle whether or not their answer could be used,
// and one of them was dead outright:
//
//   * `orderedDepIds` was assigned before the app loop and read NOWHERE. The loop went on
//     calling GetOrderedDepIds again inside the cache gate, so a run paid for both. Each call
//     builds its own DependencyResolver and EnsureIndexed is an instance field, so the second
//     resolver re-walked every package-cache directory and re-read every .app manifest out of
//     its zip, carrying nothing over from the first.
//
//   * `BcCompiler.BundleDeclaresQuery` ran ahead of the gate that is its only reason to exist.
//     A bundle that declares a query answers on its first file; one that does not — the common
//     case — reads the whole tree to prove the negative. `--no-cache` skips the gate and paid
//     for it anyway.
//
// The instrument is the phase log, which the runner already emits. Both probes now run inside
// the `alCacheDir != null` gate and record a stage when they do, so "did this run pay for the
// scan" is directly observable rather than inferred from wall clock. That matters for how this
// is tested: wall clock on a loaded machine cannot tell a skipped scan from a fast one, and
// this box runs several agents at once.
//
// The needles are the stage NAMES, and each fails independently:
//
//   with --cache     both `ordered-dep-ids` and `query-decl-probe` appear
//   with --no-cache  NEITHER appears
//
// Before the change, `ordered-dep-ids` appeared in both runs (it was emitted unconditionally,
// as a bundle stage) and `query-decl-probe` did not exist at all. So the --no-cache assertion
// is what carries the claim, and it is asserted per stage rather than as one combined check so
// a half-fix cannot pass.
//
// The phase log also enforces a rule this change had to respect: a stage must not overlap an
// app group, or #1828's stage sum double-counts app time and eats the report's "unattributed"
// line. Both stages are AppStages, because PhaseLog.BeginApp is already open where they run.
// The last test asserts that directly — they must appear on an app row, never the bundle row.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class CacheGateProbeScopeTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepIdsStage = "ordered-dep-ids";
    private const string QueryProbeStage = "query-decl-probe";

    private readonly string _root;
    private readonly string _bundle;

    public CacheGateProbeScopeTests()
    {
        _root = TestScratch.Dir("al-runner-cache-gate-probes");
        _bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(_bundle);
        // A nonce keeps the AL source unique per run so a --cache run always MISSes. A HIT
        // would still enter the gate and still record both stages, so the assertions hold
        // either way — but a MISS is the path that actually computes a cache key, which is
        // what consumes orderedDepIds.
        WriteFixture(_bundle, Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteFixture(string dir, string nonce)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "5c1e93a7-84d2-4b60-9f37-2a6e0d18b45c",
          "name": "Cache Gate Probe Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 62310, "to": 62319 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit 62311 "CGP Tests"
        {
            Subtype = Test;

            var
                Nonce: Label '{{nonce}}';

            [Test]
            procedure Arithmetic()
            begin
                if 3 + 4 <> 7 then
                    Error('sum');
                if StrLen(Nonce) <> {{nonce.Length}} then
                    Error('nonce');
            end;
        }
        """);
    }

    /// <summary>Every stage name recorded anywhere in the phase log, with the row kind it sat on.</summary>
    private static List<(string Kind, string Stage)> StagesFrom(string logPath)
    {
        var found = new List<(string, string)>();
        if (!File.Exists(logPath)) return found;
        foreach (var line in File.ReadAllLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("stages", out var stages)) continue;
            var kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "?" : "?";
            foreach (var st in stages.EnumerateObject())
                found.Add((kind, st.Name));
        }
        return found;
    }

    private (string Output, int Exit, string LogPath) Run(bool noCache, string tag)
    {
        var logPath = Path.Combine(_root, $"phases-{tag}.jsonl");
        var cacheDir = Path.Combine(_root, $"cache-{tag}");
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = Path.Combine(TestArtifacts.HomeDir() ?? "", ".al-runner", "platform-apps");
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        args.Append($" \"{_bundle}\"");
        args.Append(noCache ? " --no-cache" : $" --cache \"{cacheDir}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_PHASE_LOG"] = logPath;

        var sb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(300_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 300s.");
        }
        // WaitForExit(int) does not drain the async output callbacks; the parameterless
        // overload does. See #2496.
        proc.WaitForExit();
        return (sb.ToString(), proc.ExitCode, logPath);
    }

    [Fact]
    public void WithCache_BothGateProbesRun()
    {
        var (output, exit, logPath) = Run(noCache: false, tag: "cached");
        Assert.True(exit == 0, $"the fixture must pass. exit={exit}\n{output}");

        var stages = StagesFrom(logPath);
        Assert.True(stages.Count > 0, $"the phase log recorded no stages at all — the instrument is broken, so the "
            + $"--no-cache assertions below would pass vacuously.\nlog: {logPath}\n{output}");

        Assert.True(stages.Any(s => s.Stage == DepIdsStage),
            $"a --cache run enters the gate and must resolve ordered dep ids, recording '{DepIdsStage}'. "
            + $"saw: {string.Join(", ", stages.Select(s => $"{s.Kind}/{s.Stage}"))}");
        Assert.True(stages.Any(s => s.Stage == QueryProbeStage),
            $"a --cache run enters the gate and must probe for a query declaration, recording "
            + $"'{QueryProbeStage}'. saw: {string.Join(", ", stages.Select(s => $"{s.Kind}/{s.Stage}"))}");
    }

    [Fact]
    public void WithNoCache_NeitherGateProbeRuns()
    {
        var (output, exit, logPath) = Run(noCache: true, tag: "nocache");
        Assert.True(exit == 0, $"the fixture must pass. exit={exit}\n{output}");

        var stages = StagesFrom(logPath);
        // Guard against a vacuous pass: if the log had no stages at all, both absence
        // assertions below would hold for the wrong reason.
        Assert.True(stages.Count > 0, $"the phase log recorded no stages at all — a vacuous pass.\nlog: {logPath}\n{output}");

        // Asserted separately so a half-fix cannot pass. Before #2557 the first of these
        // failed: ordered-dep-ids was emitted unconditionally, ahead of the gate.
        Assert.False(stages.Any(s => s.Stage == DepIdsStage),
            $"--no-cache never reaches the cache-key computation, so resolving ordered dep ids is "
            + $"pure waste and '{DepIdsStage}' must not appear. saw: "
            + string.Join(", ", stages.Select(s => $"{s.Kind}/{s.Stage}")));
        Assert.False(stages.Any(s => s.Stage == QueryProbeStage),
            $"--no-cache never consults the query sidecar, so reading the whole tree to answer "
            + $"'does this bundle declare a query' is pure waste and '{QueryProbeStage}' must not "
            + $"appear. saw: " + string.Join(", ", stages.Select(s => $"{s.Kind}/{s.Stage}")));
    }

    [Fact]
    public void GateProbeStages_AreRecordedOnAnAppRow_NotTheBundleRow()
    {
        // PhaseLog's own rule: a stage must not overlap an app group, or #1828's stage sum
        // double-counts app time and manufactures overhead in the report. Both probes run after
        // BeginApp, so both must be AppStages. This is the assertion that would catch someone
        // "simplifying" either back to PhaseLog.Stage.
        var (output, exit, logPath) = Run(noCache: false, tag: "approw");
        Assert.True(exit == 0, $"the fixture must pass. exit={exit}\n{output}");

        var stages = StagesFrom(logPath);
        foreach (var name in new[] { DepIdsStage, QueryProbeStage })
        {
            var rows = stages.Where(s => s.Stage == name).ToList();
            Assert.True(rows.Count > 0, $"'{name}' must be recorded somewhere. saw: "
                + string.Join(", ", stages.Select(s => $"{s.Kind}/{s.Stage}")));
            Assert.DoesNotContain(rows, r => r.Kind == "bundle");
        }
    }

    [Fact]
    public void OrderedDepIds_IsResolvedAtMostOncePerRun()
    {
        // The hoist existed to stop the resolve happening once per app group; it never did,
        // because the hoisted value was read nowhere. The Lazy is what finally delivers it, and
        // recording the stage inside the Lazy's factory is what makes "at most once" visible.
        var (output, exit, logPath) = Run(noCache: false, tag: "once");
        Assert.True(exit == 0, $"the fixture must pass. exit={exit}\n{output}");

        var count = StagesFrom(logPath).Count(s => s.Stage == DepIdsStage);
        Assert.True(count == 1,
            $"ordered dep ids must be resolved at most once per run, so '{DepIdsStage}' must be "
            + $"recorded exactly once; saw {count}.\n{output}");
    }
}
