using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5430 with <c>--tdd --watch</c>: a field generated in a tableextension beside a package's table is registered with the record
/// engine, and the watch reset clears that registration between cycles. A cycle that edits ANOTHER file used to take the
/// incremental path from the previous cycle's baseline (which still carries the appended extension, so nothing regenerated it)
/// and failed with "extension field N from extension M not found in NCLMetaTable". A compile that registered a package extension
/// is therefore no baseline: the next cycle is a full rebuild, which generates and registers again.
/// The fixture is the clean part of <c>TddPackageMembers</c> (no object is excluded, which is what lets a baseline be recorded at
/// all - an excluded object would hide the defect by forcing a full rebuild every cycle). Spawns the real runner.
/// </summary>
public class TddPackageMemberWatchTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static readonly string[] CleanFiles =
        { "A0Existing.al", "A1FieldTests.Codeunit.al", "A2ValueTests.Codeunit.al", "A6SecondFileTests.Codeunit.al" };

    [SkippableFact]
    public async Task EditingAnotherFile_NextCycle_StillRoundTripsTheGeneratedField()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = TestScratch.Dir("al-runner-tdd-package-member-watch");
        Directory.CreateDirectory(Path.Combine(bundle, ".alpackages"));
        var fixtureTest = Path.Combine(TddPackageMemberTests.FixtureRoot, "test");
        foreach (var f in CleanFiles) File.Copy(Path.Combine(fixtureTest, f), Path.Combine(bundle, f));
        File.Copy(Path.Combine(fixtureTest, "app.json"), Path.Combine(bundle, "app.json"));
        File.WriteAllBytes(Path.Combine(bundle, ".alpackages", TddPackageMemberTests.DepFileStem + ".app"),
            TddPackageMemberTests.BuildApp(TddPackageMemberTests.DepAppId, "Tdd Package Members Dep", 65380, 65389,
                TddPackageMemberTests.DepSymbols, Path.Combine(TddPackageMemberTests.FixtureRoot, "dep")));
        File.WriteAllBytes(Path.Combine(bundle, ".alpackages", TddPackageMemberTests.TwinFileStem + ".app"),
            TddPackageMemberTests.BuildApp(TddPackageMemberTests.TwinAppId, "Tdd Package Members Twin", 65390, 65394,
                TddPackageMemberTests.TwinSymbols, Path.Combine(TddPackageMemberTests.FixtureRoot, "twin")));
        var cacheDir = Path.Combine(bundle, ".cache");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --tdd --watch --cache \"{cacheDir}\"",
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
                    throw new TimeoutException($"watch marker not seen - subprocess exited early (exit={p.ExitCode}).\n--- last output ---\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            throw new TimeoutException($"watch marker not seen.\n--- last output ---\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        // The stderr diagnostics are written by an independent pump, so (as in TddWatchTests) they are polled on the unbounded stderr
        // stream rather than read out of a stdout-bounded window.
        async Task WaitForStderrContains(string needle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                string text; lock (lines) text = WatchOutputSlicing.StderrText(lines);
                if (text.Contains(needle, StringComparison.Ordinal)) return;
                await Task.Delay(200);
            }
            string dump; lock (lines) dump = WatchOutputSlicing.StderrText(lines);
            throw new TimeoutException($"stderr never contained \"{needle}\".\n--- stderr ---\n{dump}");
        }

        try
        {
            // Cycle 1 (cold): the field is generated, registered, and round-trips through the table.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            Assert.Contains("NewField_RoundTripsThroughTheTable", cycle1);
            Assert.DoesNotContain("not found in NCLMetaTable", cycle1);
            Assert.DoesNotContain("FAIL ", cycle1);
            Assert.Contains("--tdd: generated 5 member(s) this run:", cycle1);

            // Cycle 2: a file that is NOT where the extension sits changes.
            var other = Path.Combine(bundle, "A6SecondFileTests.Codeunit.al");
            var original = await File.ReadAllTextAsync(other);
            WatchEdit.Replace(other, original + "\n// touched\n");

            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);
            Assert.DoesNotContain("not found in NCLMetaTable", cycle2);
            Assert.DoesNotContain("FAIL ", cycle2);
            Assert.Contains("PASS", cycle2);
            Assert.Contains("NewField_RoundTripsThroughTheTable", cycle2);
            // Generated again, not carried over from the cycle before.
            Assert.Contains("--tdd: generated 5 member(s) this run:", cycle2);
            // A full rebuild, and the reason names the extension: the compile of cycle 1 recorded no baseline.
            await WaitForStderrContains("FULL REBUILD", TimeSpan.FromSeconds(10));
            await WaitForStderrContains("generated a field of a package's table in a tableextension", TimeSpan.FromSeconds(10));
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
