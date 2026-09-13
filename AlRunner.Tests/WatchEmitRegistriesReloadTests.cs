// WatchEmitRegistriesReloadTests — the regression guard for #2655.
//
// AlEnumMetadataRegistry, AlReportMetadataRegistry and AlReportLayoutRegistry are filled only
// by BC's own Emit (CaptureOutputter.AddApplicationObject) and cleared by
// BcRuntime.ResetForNewBundleReload at the top of every --watch cycle. TryEmitIncremental's RAD
// fast paths return without an Emit covering the objects this cycle did not touch, so from the
// first incremental cycle the three registries stayed empty: Format(enum) gave the raw ordinal,
// Report.Run refused as "report-metadata-unavailable", and Report Layout List had no rows.
// Measured before the fix (PR body): cycle 1 PASS, every later cycle FAIL on all four tests,
// with --no-cache and with --cache.
//
// Runner-specific: real BC has no --watch and no RAD baseline. The AL assertions are ordinary BC
// behaviour used only as observables that tell a present registry entry from a missing one.
//
// Two watch sessions against ONE cache root: the first starts cold (cycle 1 compiles), the
// second starts warm (cycle 1 is an AL-output cache HIT), and both must keep passing after edits.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class WatchEmitRegistriesReloadTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "WatchEmitRegistriesReload"));

    // One test per registry, plus the enumextension half of the enum registry, so a missing
    // replay of any one of them reds exactly its own test.
    private static readonly string[] TestLabels =
    {
        "Codeunit71845.EnumValueFormatsAsItsDeclaredCaption",
        "Codeunit71845.EnumExtensionValueFormatsAsItsDeclaredCaption",
        "Codeunit71845.ReportHonoursItsDeclaredMaxIteration",
        "Codeunit71845.ReportLayoutListCarriesTheDeclaredLayout",
    };

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
    }

    // Same helper and conditional shape as WatchPageMetadataReloadTests.ExtraPackageCacheArgs:
    // CI's unit-test step provisions ~/.al-runner/platform-apps and passes it to nothing by
    // default, so a spawned subprocess has to be told about it explicitly.
    private static string[] ExtraPackageCacheArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    [SkippableFact]
    public async Task Watch_EnumReportAndLayoutMetadataSurviveLaterCycles_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = TestScratch.Dir("al-runner-watch-emitregistries");
        CopyDir(FixtureRoot, bundle);
        // Outside the repository, private to this test: the shared cache root is not keyed on
        // the runner binary.
        var cacheDir = TestScratch.Dir("al-runner-watch-emitregistries-cache");

        await RunWatchSession("cold session", bundle, cacheDir, firstMarker: 1);
        await RunWatchSession("warm session", bundle, cacheDir, firstMarker: 4);
    }

    private static async Task RunWatchSession(string session, string bundle, string cacheDir, int firstMarker)
    {
        var testsPath = Path.Combine(bundle, "WerTests.Codeunit.al");
        var lines = new List<CapturedLine>();
        var argsBuilder = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
            + $" \"{bundle}\" --watch --cache \"{cacheDir}\"");
        foreach (var a in ExtraPackageCacheArgs()) argsBuilder.Append($" \"{a}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argsBuilder.ToString(),
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

        string ProcessLiveness() => p.HasExited ? $"process alive=false exit={p.ExitCode}" : "process alive=true";
        string DumpAll() { lock (lines) return string.Join("\n", lines.Select(l => $"[{l.Stream}] {l.Text}")); }

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
                        $"{session}: watch marker not seen — subprocess exited early ({ProcessLiveness()}).\n"
                        + $"--- full subprocess output ---\n{DumpAll()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException(
                $"{session}: watch marker not seen. {ProcessLiveness()}\n--- full subprocess output ---\n{DumpAll()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        void AssertAllPass(string label, string window)
        {
            try
            {
                foreach (var t in TestLabels)
                {
                    // Contains first: a cycle in which the test did not run would satisfy the
                    // DoesNotContain("FAIL") check trivially.
                    Assert.Contains(t, window);
                    Assert.DoesNotContain($"FAIL  {t}", window);
                    Assert.Contains($"PASS  {t}", window);
                }
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"{session} {label}: {ex.Message}\n--- full subprocess output ({lines.Count} lines) ---\n{DumpAll()}", ex);
            }
        }

        try
        {
            int marker = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(300));
            AssertAllPass("cycle 1", Segment(0, marker));

            // Comment-only edits to the TEST codeunit; the enum, enumextension and both reports
            // stay byte-identical, so they are objects the app declares and did not touch.
            // Three cycles, because when the RAD fast path first engages depends on when a clean
            // baseline was recorded; asserting every cycle needs no assumption about which.
            for (int n = firstMarker + 1; n <= firstMarker + 3; n++)
            {
                var src = await File.ReadAllTextAsync(testsPath);
                var edited = src.Replace($"// EDIT-MARKER: {n - 1}", $"// EDIT-MARKER: {n}");
                Assert.NotEqual(src, edited);
                await File.WriteAllTextAsync(testsPath, edited);

                int next = await WaitForMarkerAfter(marker + 1, TimeSpan.FromSeconds(300));
                var window = Segment(marker + 1, next);
                marker = next;
                AssertAllPass($"edit {n}", window);
            }
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
