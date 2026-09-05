// WatchLayeredDependencyStaleTests — issue #2683: under `--watch`, a dependent bundle is
// served from cache after its DEPENDENCY changes, so its tests run against the previous
// compile and report a confident green for code that is no longer on disk.
//
// Mechanism (read off Program.cs, then confirmed by this test)
// -----------------------------------------------------------
// `RunLayeredPrePass` — the pass that synthesises each depended-upon bundle into a
// content-keyed workspace dir as a source `.app` plus a `*.symbols.json` sidecar — is called
// ONCE, before the `while (true)` watch loop, and never again. Every later cycle therefore
// reuses the `packageCacheDirs` and workspace dirs computed from the FIRST cycle's dependency
// sources.
//
// Two things go stale together, and either alone would be enough to produce a wrong green:
//
//   * COMPILE-TIME. The dependent resolves the dependency's symbols from the frozen
//     `*.symbols.json`, so it compiles against the previous public surface — a procedure the
//     edit deleted still resolves.
//   * RUNTIME. `DependencyLoader` extracts and compiles the dependency's source out of the
//     frozen `.app`, so even a fresh compile of the dependent would EXECUTE the previous
//     dependency code.
//
// And the cache key cannot notice. `GetOrderedDepIds` deliberately stamps each resolved
// dependency `.app` with `mtime:length` precisely so a sibling source app's changing content
// invalidates the dependent (see its own comment) — but nothing rewrites that `.app` after
// cycle 1, so the stamp is frozen and `ComputeAlCacheKey` returns the same key. The dependent
// re-emits in 0.0s, links against the old symbols, and runs the old code.
//
// That is why the fix is to re-run the pre-pass per cycle rather than to add another input to
// the cache key: invalidating the key alone would recompile the dependent against symbols and
// runtime code that are still the previous cycle's.
//
// The two arms
// ------------
// POSITIVE — edit the dependency so its OBSERVABLE ANSWER changes (42 -> 99) while everything
// still compiles. A test asserting 42 must start FAILING on the next cycle. Choosing a value
// change over deleting the procedure isolates this defect from the separate EMIT-EXCLUDED /
// compile-fail reporting concerns in the same issue: nothing here fails to compile, so a green
// cycle 2 can only mean the previous compile ran.
//
// NEGATIVE — edit ONLY the test bundle. The dependency's content is unchanged, so the pre-pass
// must report `cache HIT` for it rather than re-synthesising. Without this arm the positive one
// is satisfied by simply rebuilding the world every cycle, which is what `--watch` exists not
// to do.
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public class WatchLayeredDependencyStaleTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepAppId = "b7c1d2e3-2683-4a11-9111-111111111111";
    private const string TestAppId = "b7c1d2e3-2683-4a11-9222-222222222222";

    private static string WriteDepBundle(string root, int answer)
    {
        var dir = Path.Combine(root, "dep-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{DepAppId}}",
          "name": "Watch Layered Dep WLD",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60420, "to": 60429 } ],
          "runtime": "14.0"
        }
        """);
        WriteDepSource(dir, answer);
        return dir;
    }

    // The single file the positive arm edits. Exactly one object, one procedure, one literal —
    // nothing that could make the dependency's own recompile take a path other than the normal
    // one.
    private static void WriteDepSource(string dir, int answer) =>
        File.WriteAllText(Path.Combine(dir, "Answer.Codeunit.al"), $$"""
        codeunit 60420 "WLD Answer"
        {
            procedure Value(): Integer
            begin
                exit({{answer}});
            end;
        }
        """);

    private static string WriteTestBundle(string root, string extraComment)
    {
        var dir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{TestAppId}}",
          "name": "Watch Layered Dep Tests WLD",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{DepAppId}}", "name": "Watch Layered Dep WLD",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60430, "to": 60439 } ],
          "runtime": "14.0"
        }
        """);
        WriteTestSource(dir, extraComment);
        return dir;
    }

    // Asserts the dependency's answer is 42 and names the value it actually saw, so a failure
    // says WHICH compile ran rather than only that one did.
    private static void WriteTestSource(string dir, string extraComment) =>
        File.WriteAllText(Path.Combine(dir, "AnswerTests.Codeunit.al"), $$"""
        // {{extraComment}}
        codeunit 60430 "WLD Answer Tests"
        {
            Subtype = Test;

            [Test]
            procedure DependencyAnswerIs42()
            var
                Answer: Codeunit "WLD Answer";
            begin
                if Answer.Value() <> 42 then
                    Error('WLD dependency answered %1, expected 42', Answer.Value());
            end;
        }
        """);

    [SkippableFact]
    public async Task Watch_DependencyEditedAfterCycleOne_IsNotServedFromThePreviousCompile()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-watch-layered-stale", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var depDir = WriteDepBundle(root, answer: 42);
        var testDir = WriteTestBundle(root, extraComment: "cycle 1");
        var cacheDir = Path.Combine(root, ".cache");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{depDir}\" \"{testDir}\" --watch --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, OutputStream stream) => Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null) lock (lines) lines.Add(new CapturedLine(stream, l));
        });
        Pump(p.StandardOutput, OutputStream.Stdout);
        Pump(p.StandardError, OutputStream.Stderr);

        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(60).Select(l => $"[{l.Stream}] {l.Text}")); }

        async Task<int> WaitForMarkerAfter(int fromIndex, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited)
                {
                    await Task.Delay(500);
                    throw new TimeoutException(
                        $"watch marker not seen — subprocess exited early (exit={p.ExitCode}).\n--- last output ---\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException($"watch marker not seen.\n--- last output ---\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        try
        {
            // ── Cycle 1: the baseline. The dependency answers 42 and the test passes.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(240));
            var cycle1 = Segment(0, m1);
            Assert.True(cycle1.Contains("PASS"), "cycle 1 did not pass:\n" + cycle1);
            Assert.DoesNotContain("FAIL", cycle1);

            // ── Cycle 2 (POSITIVE): the dependency now answers 99. Nothing fails to compile;
            // the only way the test can still pass is by running the previous compile.
            WriteDepSource(depDir, answer: 99);
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);

            // Assert.True with the whole cycle as the message: when this regresses, the
            // question is always "which compile ran", and that is only answerable from the
            // cycle's own output.
            Assert.True(cycle2.Contains("FAIL"), "cycle 2 reported no FAIL:\n" + cycle2);
            Assert.True(cycle2.Contains("WLD dependency answered 99"),
                "cycle 2 did not run the edited dependency:\n" + cycle2);

            // ── Cycle 3 (NEGATIVE): only the TEST bundle changes. The dependency's content is
            // identical to cycle 2's, so the pre-pass must serve it from its content-keyed
            // workspace dir instead of synthesising it again — otherwise the fix has replaced a
            // stale cache with a full rebuild on every keystroke.
            WriteTestSource(testDir, extraComment: "cycle 3");
            int m3 = await WaitForMarkerAfter(m2 + 1, TimeSpan.FromSeconds(240));
            var cycle3 = Segment(m2 + 1, m3);

            Assert.True(cycle3.Contains("[layered] cache HIT Watch Layered Dep WLD"),
                "cycle 3 re-synthesised an unchanged dependency:\n" + cycle3);
            Assert.DoesNotContain("[layered] WROTE Watch Layered Dep WLD", cycle3);
            // Still failing, and still for the right reason: cycle 3 did not revert to 42.
            Assert.True(cycle3.Contains("WLD dependency answered 99"),
                "cycle 3 did not run the edited dependency:\n" + cycle3);

            // ── Cycle 4 (WARM): put the dependency back to 42. Its content is one this
            // process has already compiled, so both the layered workspace dir and the Tier-3
            // compiled-deps entry are HITS — and this is where a fix that only ever moved
            // FORWARD would show: serving the cached 99 module for 42 source is the same
            // defect pointing the other way, and a cache HIT is exactly the path that gets
            // it wrong. See .claude/rules/local-test-scope.md on running cache-sensitive
            // changes warm as well as cold.
            WriteDepSource(depDir, answer: 42);
            int m4 = await WaitForMarkerAfter(m3 + 1, TimeSpan.FromSeconds(240));
            var cycle4 = Segment(m3 + 1, m4);

            Assert.True(cycle4.Contains("PASS"), "cycle 4 did not go back to passing:\n" + cycle4);
            Assert.DoesNotContain("FAIL", cycle4);
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
