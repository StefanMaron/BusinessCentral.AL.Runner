// WatchCrossBundleTableSchemaTests — end-to-end coverage for an arrangement that had NONE: an app
// bundle that DEFINES A TABLE plus a test bundle that does Record operations on that table, run
// under `--watch` across several cycles.
//
// WHAT THIS IS NOT
// ----------------
// It is not a reproduction of #2823, and it must not be described as one. #2823 asked whether
// --watch's per-bundle BcRuntime.ResetForNewBundleReload() wipes an app bundle's parsed table
// schemas before its test bundle runs — the failure the --server call site records as
// "no NCLMetaTable for table N (AL source not parsed)". Measured, it does not, and #2823 closed
// on that measurement:
//
//   * _parsedTables is EMPTY at every per-bundle reset under --watch (instrumented: "cleared
//     1 bc app path(s), 0 parsed table(s)", twice). The reset does clear that cache; there is
//     simply nothing in it to lose.
//   * The dependency's table reaches the consumer as a synthesized package from the layered
//     pre-pass ("[layered] cache HIT ... src .app + sidecar symbols"), not by cross-bundle
//     parsed-source accumulation.
//   * Probing the other direction — clearing the registered .app paths in that same reset —
//     also stayed green, because each bundle re-registers its own dependencies during its own
//     dependency resolution, which runs AFTER the reset in the same iteration.
//
// So: everything the per-bundle reset clears is re-established by the very bundle whose
// iteration it precedes. NO mutation attempted made this test red, including destroying the
// dependency .app registration. It guards an arrangement, not a known defect, and saying
// otherwise would make it the thing #2801 describes and tdd.md forbids — a test that cannot
// fail for the reason it names.
//
// WHAT WOULD MAKE IT FAIL
// -----------------------
// Worth stating, because a guard whose failure mode nobody can describe decays into noise the
// first time it goes red for an unrelated reason:
//
//   * The layered pre-pass stops synthesizing a declared dependency, so the consumer cannot
//     resolve the dependency's table. Asserted DIRECTLY rather than left to luck — see the
//     [layered] assertion below — so the route going away is caught even if the run still passes
//     by some other path. That assertion's teeth were demonstrated, not argued: the same runner
//     over a SINGLE bundle with no cross-bundle dependency emits 0 [layered] lines, against 3
//     for this fixture. It fails when the route is absent.
//   * The cross-bundle Record round-trip breaks: insert, Get by primary key, or reading back a
//     field VALUE from a dependency-defined table. The test asserts the value, so a schema that
//     silently yields defaults cannot pass.
//   * A watch cycle stops re-running all bundles, or stops reporting, so a later cycle never
//     arrives (surfaces as the cycle-marker timeout).
//   * And the one that connects back to #2823: a future change that makes the per-bundle reset
//     clear something NOT re-established within that bundle's own iteration. That is exactly the
//     hazard the --server comment records, and this arrangement is where it would show up first
//     if it ever became reachable again.
//
// Three cycles, because the reset's own comment says "No-op on the first iteration (caches
// already empty)" — that is the first BUNDLE iteration, not the first cycle, so cycle 1 is
// meaningful too, and an edit to each side is covered separately.
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public class WatchCrossBundleTableSchemaTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string AppId = "d4e5f6a7-2823-4a11-9111-111111111111";
    private const string TestAppId = "d4e5f6a7-2823-4a11-9222-222222222222";
    private const int TableId = 60640;
    private const int TestsId = 60641;

    /// <summary>The dependency app: it DEFINES A TABLE. That is the whole point — the reset under
    /// test clears parsed table schemas, so a dependency without one cannot show this.</summary>
    private static string MakeAppBundle(string root)
    {
        var dir = Path.Combine(root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{AppId}}",
          "name": "Watch Schema App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{TableId}}, "to": {{TableId + 4}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tbl.al"), $$"""
        table {{TableId}} "Watch Schema Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
                field(2; Payload; Text[50]) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """);
        return dir;
    }

    /// <summary>The consumer: Record ops on the DEPENDENCY's table, asserting a field VALUE read
    /// back. A cleared schema cannot satisfy this by returning a default.</summary>
    private static void WriteTestSource(string dir, string marker)
    {
        File.WriteAllText(Path.Combine(dir, "Tests.al"), $$"""
        codeunit {{TestsId}} "Watch Schema Tests"
        {
            Subtype = Test;

            [Test]
            procedure InsertsAndReadsBackFromTheDependencysTable()
            var
                Row: Record "Watch Schema Row";
                ReadBack: Record "Watch Schema Row";
            begin
                // marker: {{marker}}
                Row."Entry No." := 1;
                Row.Payload := 'kept';
                Row.Insert();

                if not ReadBack.Get(1) then
                    Error('SCHEMA-GET-FAILED');
                if ReadBack.Payload <> 'kept' then
                    Error('SCHEMA-VALUE=' + ReadBack.Payload);
            end;
        }
        """);
    }

    private static string MakeTestAppBundle(string root, string marker)
    {
        var dir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{TestAppId}}",
          "name": "Watch Schema Test App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{AppId}}", "name": "Watch Schema App",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{TestsId}}, "to": {{TestsId + 4}} } ],
          "runtime": "14.0"
        }
        """);
        WriteTestSource(dir, marker);
        return dir;
    }

    /// <summary>
    /// A <c>--watch</c> run over an app bundle that defines a table plus a test bundle that does
    /// Record ops on it must answer the same as the equivalent one-shot CLI run, on EVERY cycle.
    ///
    /// <para>The specific failure #2823 asks about is
    /// <c>no NCLMetaTable for table N (AL source not parsed)</c> — the app bundle's parsed schema
    /// having been cleared by the test bundle's own per-bundle reset before the test bundle ran.
    /// It is asserted by name and separately from the pass/fail bar, so a regression that goes red
    /// some other way cannot satisfy this test.</para>
    /// </summary>
    [SkippableFact]
    public async Task WatchCycles_WithATableDefiningDependency_NeverLoseTheParsedSchema()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-watch-schema");
        Directory.CreateDirectory(root);
        var appDir = MakeAppBundle(root);
        var testAppDir = MakeTestAppBundle(root, "cycle-1");

        // Outside the repository: a --cache inside a worktree has faked a whole-bundle install
        // failure before.
        var cacheDir = Path.Combine(root, "cache");
        Directory.CreateDirectory(cacheDir);

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                // No --verbose needed, and that was checked rather than assumed: the layered
                // pre-pass runs BEFORE the bundle loop, so its output precedes the point where
                // the watch dashboard sets Console.Out and Console.Error to TextWriter.Null for
                // the cycle body. Diagnostics emitted INSIDE a cycle are the ones that vanish
                // without --verbose — that is what made an instrumentation probe read as dead
                // code while investigating #2823, and nearly produced the opposite conclusion.
                + $" \"{appDir}\" \"{testAppDir}\" --watch --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, OutputStream stream) => _ = Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null)
                lock (lines) lines.Add(new CapturedLine(stream, l));
        });
        Pump(p.StandardOutput, OutputStream.Stdout);
        Pump(p.StandardError, OutputStream.Stderr);

        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(80).Select(l => $"[{l.Stream}] {l.Text}")); }

        async Task<int> WaitForMarkerAfter(int fromIndex, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(
                        lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited)
                {
                    await Task.Delay(500);
                    throw new TimeoutException(
                        $"watch marker not seen — subprocess exited early (exit={p.ExitCode}).\n"
                        + $"--- last output ---\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException($"watch marker not seen.\n--- last output ---\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        void AssertCycleHealthy(string cycle, string label)
        {
            // The INVARIANT, not just the outcome. #2823 established that this arrangement is
            // safe *because* the dependency arrives as a layered-synthesized package rather than
            // through cross-bundle parsed-source accumulation. Asserting the route means the test
            // fails when that stops being true, instead of only when the consequence happens to
            // surface — which is the difference between a guard and a green light.
            Assert.True(cycle.Contains("[layered]", StringComparison.Ordinal),
                $"{label}: the layered pre-pass did not report on the declared dependency. That "
                + "route is what serves the dependency's table to the consumer; if it is gone, "
                + "this arrangement's safety argument (#2823) no longer holds and the parsed-schema "
                + "hazard the --server call site records becomes reachable again.\n" + cycle);

            // Named separately from the pass/fail bar: this is the specific failure #2823 is
            // about, and a regression that reddens the cycle some other way must not be able to
            // satisfy this assertion.
            Assert.False(cycle.Contains("no NCLMetaTable", StringComparison.Ordinal),
                $"{label}: the dependency's parsed table schema was gone when the test bundle ran "
                + "— the per-bundle BcRuntime.ResetForNewBundleReload() cleared it, which is what "
                + "the --server call site records as having broken app + test-app pairs "
                + "(#2823).\n" + cycle);
            Assert.False(cycle.Contains("SCHEMA-GET-FAILED", StringComparison.Ordinal),
                $"{label}: the row inserted into the dependency's table could not be read back.\n" + cycle);
            Assert.True(cycle.Contains("PASS", StringComparison.Ordinal)
                        && !cycle.Contains("FAIL", StringComparison.Ordinal),
                $"{label}: a --watch cycle must answer as the equivalent one-shot CLI run does.\n" + cycle);
        }

        try
        {
            // ── Cycle 1. The reset is NOT cycle-gated, so it already fires between bundle A and
            // bundle B within this cycle — if the hazard is real at all, it can show up here.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(300));
            AssertCycleHealthy(Segment(0, m1), "cycle 1");

            // ── Cycle 2. Edit ONLY the test bundle, so the dependency's own source is untouched
            // and the question is purely whether its parsed schema survived into this cycle.
            WriteTestSource(testAppDir, "cycle-2");
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(300));
            AssertCycleHealthy(Segment(m1 + 1, m2), "cycle 2 (test bundle edited)");

            // ── Cycle 3. Now edit the DEPENDENCY, which forces it to re-parse and re-emit while
            // the consumer's files are unchanged — the other direction of the same question.
            File.WriteAllText(Path.Combine(appDir, "Tbl.al"), $$"""
            table {{TableId}} "Watch Schema Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
                    field(2; Payload; Text[50]) { DataClassification = CustomerContent; }
                    field(3; Extra; Integer) { DataClassification = CustomerContent; }
                }
                keys { key(PK; "Entry No.") { Clustered = true; } }
            }
            """);
            int m3 = await WaitForMarkerAfter(m2 + 1, TimeSpan.FromSeconds(300));
            AssertCycleHealthy(Segment(m2 + 1, m3), "cycle 3 (dependency edited)");
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
