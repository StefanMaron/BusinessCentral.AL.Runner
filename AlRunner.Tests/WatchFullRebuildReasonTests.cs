using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1905 (defect 4): when a `--watch` cycle falls back to a full rebuild (instead of
/// the proportional-cost incremental path), the reason must reach the console at
/// DEFAULT verbosity — not only under `--verbose`. A full rebuild costs whole minutes
/// on a large app (761-862s measured on NP Retail, #1905's own numbers), so which
/// reason forced it is a RESULT the developer needs, exactly like which BC version was
/// selected (Log.cs's `[bc]` history) and whether the expectations manifest was found
/// (`[expectations]`, #1984) — both were previously swallowed by the same
/// --verbose-only gate and both cost real, measured damage before being exempted.
///
/// Also proves the inverse the "judgment" half of #1905's ask cares about: the very
/// first --watch cycle for a bundle ALWAYS falls back (there is no baseline yet), and
/// that is not a fallback in any meaningful sense — printing an alarming "full
/// rebuild" line on every single startup would train the reader to ignore it, so cycle
/// 0 must stay quiet and only cycle 1+ is asserted here.
///
/// Runs the real CLI in non-interactive (piped) watch mode, so it exercises
/// Program.cs's plain-line fallback branch (WatchDashboard's own interactive-frame
/// surfacing is covered separately, and purely, in WatchDashboardTests — this test
/// proves the wiring that actually reaches Program.cs's console output end-to-end).
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class WatchFullRebuildReasonTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RecordTriggerXRec"));

    [SkippableFact]
    public async Task Watch_FullRebuildFallback_ReasonReachesDefaultVerbosityOutput_ButNotOnFirstCycle()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = Path.Combine(Path.GetTempPath(), "al-runner-watch-fullrebuild", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var testsCodeunitPath = Path.Combine(bundle, "XRecProbeTests.Codeunit.al");
        var cacheDir = Path.Combine(bundle, ".cache");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // Deliberately NO --verbose: this is the whole claim under test — the
            // reason must reach default-verbosity output.
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
            // Cycle 0 (cold): the fixture always falls back here too ("no incremental
            // baseline yet") — but that fallback must NOT be reported as a "full
            // rebuild" alarm, since every single --watch invocation hits it.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            Assert.DoesNotContain("FULL REBUILD", cycle1);

            // Force a genuine fallback on cycle 1 via an ACTUAL .al edit (app.json isn't
            // watched at all — WatchSource.cs filters strictly to "*.al", so an app.json-only
            // edit would never even trigger a cycle): append a second object declaration to
            // an already-tracked .al file. Per BcCompiler.Incremental.cs's
            // ClassifyDeclaredObject, the fast path requires exactly one object per file, so
            // this is a reliable, deterministic fallback with a specific, recognisable cause.
            var original = await File.ReadAllTextAsync(testsCodeunitPath);
            var edited = original + "\ncodeunit 60199 \"xRec Probe Extra RXT\"\n{\n}\n";
            await File.WriteAllTextAsync(testsCodeunitPath, edited);

            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);

            // The reason reached the console WITHOUT --verbose (the defect) and names
            // the specific cause, not a generic "something changed" — a reader must be
            // able to tell WHY the cycle cost minutes instead of milliseconds.
            Assert.Contains("FULL REBUILD", cycle2);
            Assert.Contains("XRecProbeTests.Codeunit.al", cycle2);
            Assert.Contains("declares 2 object(s)", cycle2);
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
