// WatchCrossAppOverloadRebindTests — issue #2815: does `--watch` share #2614's bundle-order
// defect, which #2814 fixed for `--server`?
//
// #2815 is an unmeasured QUESTION, filed by impl-1 while fixing #2614 rather than answered by
// assumption, and this file is the experiment it names. The two paths stay resident across
// cycles, which is the property that made `--server` vulnerable, but they do not share a reload
// discipline:
//
//   --server   BcRuntime.ResetForNewBundleReload() once per REQUEST, before the bundle loop
//   --watch    BcRuntime.ResetForNewBundleReload() once per BUNDLE, inside the loop
//
// A per-bundle reset plausibly closes the stale-assembly window or plausibly does not — the
// reset clears bundle-derived caches (record/codeunit types, parsed schemas, in-memory rows,
// enum registry), which is not obviously the same thing as the resident ASSEMBLY a cross-app
// call dispatches into. Reading it settles nothing, so this measures it.
//
// The fixture is ServerCrossAppOverloadRebindTests', unchanged in substance: a dependency app
// declaring Which(Decimal), a consumer passing an INTEGER, and an edit that adds Which(Integer)
// beside it. Adding an overload leaves the existing member's id alone, so what moves is the id
// the CALLER bakes — which is why the failure can be silent rather than a missing-member throw.
//
// Both orders are driven, because the order is the whole variable:
//
//   dependency FIRST  — the documented order, and the control. If this fails, the defect is not
//                       about ordering at all and the conclusion below would be wrong.
//   dependency LAST   — the shape that broke --server. `--watch` accepts multi-bundle runs (its
//                       only guard is `jobs > 1 && bundles.Count > 1 && !watchMode`, which
//                       excludes watch from parallel sharding, not from multiple bundles), so
//                       this ordering is reachable.
//
// Failure modes this distinguishes, all three of which have been real in the --server path:
//
//   BOUND-TO=1                        the silent one: the consumer dispatched the member id
//                                     Which(Decimal) resolved to, that member still exists, so
//                                     the call succeeded and returned the previous answer
//   NavNCLCompilationException        the loud one #2603 converted the silent one into
//   BOUND-TO=2                        correct: same answer a cold run of these sources gives
//
// If this reproduces, the fix is BundleDependencyOrder.Sort — already extracted, pure and
// unit-tested for #2614 — applied to the watch loop's bundle list, NOT a second spelling of the
// same rule. If it does not reproduce, that is the answer #2815 asks for and this file is the
// record of it, so nobody re-asks.
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public class WatchCrossAppOverloadRebindTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    // One id set per ordering case, so the two cases cannot share on-disk or in-process state.
    private static string AppId(int c) => $"c3d4e5f6-2815-4a11-9111-11111111111{c}";
    private static string TestAppId(int c) => $"c3d4e5f6-2815-4a11-9222-22222222222{c}";
    private static int LibId(int c) => 60560 + c * 20;
    private static int TestsId(int c) => 60570 + c * 20;

    private static string LibBefore(int c) => $$"""
        codeunit {{LibId(c)}} "Watch Ovl Lib {{c}}"
        {
            procedure Which(Seed: Decimal): Integer
            begin
                exit(1);
            end;
        }
        """;

    /// <summary>The edit: a second overload of the SAME name taking Integer. No existing
    /// member's id moves — only the id the caller binds.</summary>
    private static string LibAfter(int c) => $$"""
        codeunit {{LibId(c)}} "Watch Ovl Lib {{c}}"
        {
            procedure Which(Seed: Decimal): Integer
            begin
                exit(1);
            end;

            procedure Which(Seed: Integer): Integer
            begin
                exit(2);
            end;
        }
        """;

    private static string MakeAppBundle(string root, int c, string lib)
    {
        var dir = Path.Combine(root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{AppId(c)}}",
          "name": "Watch Ovl App {{c}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{LibId(c)}}, "to": {{LibId(c) + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Lib.al"), lib);
        return dir;
    }

    /// <summary>The consumer. Byte-identical across every cycle, and it passes an INTEGER — so
    /// what it binds to is decided entirely by which overloads the dependency declares.</summary>
    private static string MakeTestAppBundle(string root, int c)
    {
        var dir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{TestAppId(c)}}",
          "name": "Watch Ovl Test App {{c}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{AppId(c)}}", "name": "Watch Ovl App {{c}}",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{TestsId(c)}}, "to": {{TestsId(c) + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), $$"""
        codeunit {{TestsId(c)}} "Watch Ovl Tests {{c}}"
        {
            Subtype = Test;

            [Test]
            procedure BindsTheIntegerOverload()
            var
                Lib: Codeunit "Watch Ovl Lib {{c}}";
                Seed: Integer;
                Bound: Integer;
            begin
                Seed := 7;
                Bound := Lib.Which(Seed);
                // Deliberately the POST-edit expectation, so the consumer's own source can stay
                // byte-identical across cycles — a modified caller would get a fresh call-site id
                // anyway and there would be nothing to measure. The value is in the message so a
                // wrong answer names itself instead of only failing.
                if Bound <> 2 then
                    Error('BOUND-TO=' + Format(Bound));
            end;
        }
        """);
        return dir;
    }

    /// <summary>
    /// A warm <c>--watch</c> cycle whose DEPENDENCY app gained an overload must answer exactly as
    /// a cold run of the same sources does, in EITHER bundle order.
    ///
    /// <para><paramref name="dependencyFirst"/> is the control/experiment split described in this
    /// file's header: dependency-first is the documented order and must pass for the
    /// dependency-last result to mean anything about ordering.</para>
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WarmWatchCycle_AfterADependencyGainsAnOverload_NeverAnswersWithTheOldOverload(
        bool dependencyFirst)
    {
        TestArtifacts.SkipIfMissing();

        var c = dependencyFirst ? 1 : 2;
        var root = TestScratch.Dir("al-runner-watch-xapp-overload");
        Directory.CreateDirectory(root);
        var appDir = MakeAppBundle(root, c, LibBefore(c));
        var testAppDir = MakeTestAppBundle(root, c);

        // Outside the repository on purpose: a --cache pointed inside a worktree has faked a
        // whole-bundle install failure before.
        var cacheDir = Path.Combine(root, "cache");
        Directory.CreateDirectory(cacheDir);

        var ordered = dependencyFirst
            ? new[] { appDir, testAppDir }
            : new[] { testAppDir, appDir };

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{ordered[0]}\" \"{ordered[1]}\" --watch --cache \"{cacheDir}\"",
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

        try
        {
            // ── Cycle 1: pre-edit. The Integer argument widens to Which(Decimal), so the test
            // reports BOUND-TO=1 and fails. Asserted rather than tolerated: it is the measurement
            // that the fixture really does start bound to the Decimal overload, so cycle 2 cannot
            // pass by the answer having been 2 all along.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(300));
            var cycle1 = Segment(0, m1);
            Assert.True(cycle1.Contains("BOUND-TO=1", StringComparison.Ordinal),
                "cycle 1 did not bind the Decimal overload, so the fixture proves nothing about "
                + "cycle 2:\n" + cycle1);

            // ── Edit ONLY the dependency. The consumer's files are not touched.
            File.WriteAllText(Path.Combine(appDir, "Lib.al"), LibAfter(c));

            // ── Cycle 2: same resident process, warm dependencies. A cold run of these sources
            // binds Which(Integer) and answers 2.
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(300));
            var cycle2 = Segment(m1 + 1, m2);

            // The floor, asserted separately from the pass/fail bar: the previous overload's
            // answer must never come back reported as a result. This is the SILENT failure, and a
            // regression that made the cycle loudly red instead must not satisfy this assertion.
            Assert.False(cycle2.Contains("BOUND-TO=1", StringComparison.Ordinal),
                $"(dependencyFirst: {dependencyFirst}) the consumer returned the PREVIOUS "
                + "overload's answer after its dependency gained an overload. Its own files did "
                + "not change, so its module replayed the last cycle's result and kept dispatching "
                + "the member id Which(Decimal) resolved to — a member that still exists, so the "
                + "call succeeded with no exception and no diagnostic.\n" + cycle2);

            // And the loud shape, named so a regression reports which of the two it is rather
            // than only that the cycle went red.
            Assert.False(cycle2.Contains("does not have a member with that ID", StringComparison.Ordinal),
                $"(dependencyFirst: {dependencyFirst}) the dependency's runtime ASSEMBLY had not "
                + "been reloaded when the consumer's tests ran — the loud half of #2614, which "
                + "BundleDependencyOrder.Sort fixed for --server by executing dependencies "
                + "first.\n" + cycle2);

            Assert.True(cycle2.Contains("PASS", StringComparison.Ordinal)
                        && !cycle2.Contains("FAIL", StringComparison.Ordinal),
                $"(dependencyFirst: {dependencyFirst}) a warm --watch cycle must answer exactly as "
                + "a cold run of these sources does (BOUND-TO=2).\n" + cycle2);
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
