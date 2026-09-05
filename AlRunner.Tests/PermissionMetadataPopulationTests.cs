// PermissionMetadataPopulationTests — issue #2893.
//
// A RUNNER-MECHANISM test. The claim is about the runner's own skeleton state, not about what
// BC does: after a run has touched permission sets, BC's own permission metadata layer must be
// able to answer for every permission set the runner knows —
//
//   * NavCurrentThread.ResolveAppGroup().PermissionSetGroupObjectMetadataSummaries carries one
//     entry per known permission set (measured at 0 before this fix, for every object type,
//     not just permission sets), and
//   * NCLMetadata.TryGetMetaPermissionSetById resolves each of those ids (measured returning
//     False with a null out param before this fix).
//
// Why a diagnostic line and not an AL assertion: both structures are internal engine state that
// no AL surface exposes today. BC's PermissionDataProviderBase reads them, and the three tables
// it serves — Permission (2000000005), Metadata Permission (2000000251), Expanded Permission
// (2000000254) — need permission ROWS as well, which need each set's mask array out of
// SymbolReference.json (#2886). So the honest observable for THIS issue is the state itself.
//
// The fixture is deliberately the existing Fixtures/AggregatePermissionSet bundle rather than a
// new one: it already declares a permission set from source, already reads a permission table
// (which is what makes the population run), and already carries no Base Application floor. A
// second fixture would have cost another runner spawn on every CI leg for the same evidence.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class PermissionMetadataPopulationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "AggregatePermissionSet");

    private static readonly Regex DiagLine = new(
        @"\[perm-metadata\] app-group permission-set summaries: (?<summaries>\d+); "
        + @"meta permission sets resolvable: (?<resolvable>\d+)/(?<known>\d+); "
        + @"declared permissions: (?<permissions>\d+)",
        RegexOptions.Compiled);

    [Fact]
    public void AfterAPermissionTableIsTouched_TheAppGroupAndTheMetadataLayerBothAnswer()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-permmeta-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);
            var all = stdout + stderr;

            var m = DiagLine.Match(all);
            Assert.True(m.Success,
                "the run printed no [perm-metadata] diagnostic, so the permission metadata layer was "
                + "never populated:\n" + Tail(all));

            var summaries = int.Parse(m.Groups["summaries"].Value);
            var resolvable = int.Parse(m.Groups["resolvable"].Value);
            var known = int.Parse(m.Groups["known"].Value);

            // The app-group half. 0 is what this reported before the fix — for every object
            // type, not only permission sets — and 0 is what a population that lands after the
            // LazyEx has been forced would report too, which is the trap this fix is shaped
            // around.
            Assert.True(summaries > 0,
                $"NavAppGroup.PermissionSetGroupObjectMetadataSummaries carries {summaries} entries; "
                + "BC's PermissionDataProviderBase loops that dictionary, so an empty one makes it "
                + "yield nothing and answer empty instead of failing.");

            // Not merely non-empty: EVERY permission set the runner knows has to be there, or
            // the provider's loop silently skips whichever ones are missing.
            Assert.Equal(known, summaries);

            // The metadata half, exercised through BC's own TryGetMetaPermissionSetById for
            // every id — the lookup that returned False with a null out param before.
            Assert.Equal(known, resolvable);

            // #2910: the sets must carry their PERMISSIONS, not just resolve. Zero here is
            // what the metadata layer reported before the masks were transcribed out of
            // SymbolReference.json — resolvable sets that grant nothing, which composes to an
            // empty permission table however correctly BC walks them.
            var permissions = int.Parse(m.Groups["permissions"].Value);
            Assert.True(permissions > 0,
                $"the {known} resolvable permission sets declare {permissions} permissions between "
                + "them; BC's PermissionSetGraphWalker/PermissionComposer can only compose rows out "
                + "of permissions that are actually there.");

            // And the run itself still passes: the fixture's four Aggregate Permission Set
            // tests are the regression guard that populating shared app-group state did not
            // disturb the permission tables the runner already served.
            Assert.True(exit == 0, $"fixture run exited {exit}:\n" + Tail(all));
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    private static string Tail(string s)
    {
        var lines = s.Split('\n');
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 40)));
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        sb.Append(' ').Append($"\"{FixtureDir}\"");
        sb.Append(' ').Append($"--cache \"{cacheDir}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = sb.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_DIAG_PERMMETA"] = "1";

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 120s.");
        }
        // WaitForExit(int) does not wait for the async read callbacks to drain; only the
        // parameterless overload does. Without this the diagnostic line can still be in flight
        // when the assertions read it — the intermittent-on-a-loaded-machine failure documented
        // on AggregatePermissionSetVirtualTableTests.
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }
}
