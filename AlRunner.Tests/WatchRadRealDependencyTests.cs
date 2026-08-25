using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2009: `--watch`'s incremental (RAD) recompile path always fell back to a full
/// rebuild, from cycle 2 onward, for any bundle whose dependency set includes real
/// (package-scanner-resolved) app references — which is the normal shape for almost any
/// realistic AL app (RecordTriggerXRec's `app.json` has empty `dependencies` but
/// `"runtime": "14.0"`, which pulls in the 5 implicit MS-app deps: System, Application, Base
/// Application, Business Foundation, System Application).
///
/// Root cause (confirmed by adding temporary diagnostics to `RadSelfBaselineLoader` and
/// observing which method actually returned the wrong value before `CreateForRad`'s
/// "could not be loaded" diagnostics fired): `RadSelfBaselineLoader.LoadModuleInfo` and
/// `.GetDependencies` (`AlRunner/BcCompiler.Incremental.cs`) signalled "not mine" by
/// returning `null!` / `Enumerable.Empty&lt;&gt;()`, instead of throwing
/// `FileNotFoundException` — the convention `CompositeSymbolReferenceLoader`
/// (`AlRunner/SymbolJson.cs`) actually falls through on for those two methods (only
/// `LoadModule` has an explicit non-null check). Sitting FIRST in the composite chain, its
/// null/empty "miss" WAS the composite's final answer for every MS package spec, so
/// `refLoader` (the real package scanner, second in the chain) was never consulted —
/// verbatim the failure mode `JsonSymbolReferenceLoader.GetDependencies`'s own comment
/// warns about ("would WIN the composite race and erase the real dependency list").
///
/// This test proves the FAST PATH is actually taken, not merely that the build succeeds
/// (it always succeeded before too, just via the slow full-rebuild fallback): it asserts
/// cycle 2's console output — after a trivial single-literal edit that does not touch any
/// of the documented disqualifying conditions — does NOT contain "FULL REBUILD".
///
/// Spawns the real runner against the real BC artifact/package cache; skips (no-op) when
/// that cache is absent.
/// </summary>
public class WatchRadRealDependencyTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RecordTriggerXRec"));

    [SkippableFact]
    public async Task Watch_TrivialEditOnBundleWithRealMsAppDeps_TakesRadFastPath_NotFullRebuild()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = Path.Combine(Path.GetTempPath(), "al-runner-watch-rad-msdep", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var testsCodeunitPath = Path.Combine(bundle, "XRecProbeTests.Codeunit.al");
        var cacheDir = Path.Combine(bundle, ".cache");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --watch --cache \"{cacheDir}\"",
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

        string ProcessLiveness() =>
            p.HasExited ? $"process alive=false exit={p.ExitCode}" : "process alive=true";
        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(40).Select(l => $"[{l.Stream}] {l.Text}")); }

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
                        $"watch marker not seen — subprocess exited early ({ProcessLiveness()}).\n--- last output ---\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException($"watch marker not seen. {ProcessLiveness()}\n--- last output ---\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        try
        {
            // Cycle 0 (cold): always falls back ("no incremental baseline yet") — not the
            // claim under test, just getting a baseline recorded.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            Assert.DoesNotContain("FULL REBUILD", cycle1);

            // Trivial content edit: change ONE string literal inside a procedure body.
            // Still exactly one object in the file, no rename, no app.json change, no
            // duplicate declaration — none of BcCompiler.Incremental.cs's documented
            // disqualifying conditions apply, so this must take the RAD fast path.
            var original = await File.ReadAllTextAsync(testsCodeunitPath);
            Assert.Contains("'A1'", original);
            var edited = original.Replace("'A1'", "'A2'");
            await File.WriteAllTextAsync(testsCodeunitPath, edited);

            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);

            // The actual claim: RAD's fast path was taken, not the full-rebuild fallback.
            // Before the fix, this cycle prints "FULL REBUILD ... RAD Emit failed: A
            // package with publisher 'Microsoft', name '...', ... could not be loaded."
            // for all 5 implicit MS-app deps, every single cycle.
            Assert.DoesNotContain("FULL REBUILD", cycle2);
            Assert.DoesNotContain("could not be loaded", cycle2);
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
