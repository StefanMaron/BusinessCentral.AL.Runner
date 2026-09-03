// AggregatePermissionSetVirtualTableTests — issue #2357.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that
// OUR OWN population of the "Aggregate Permission Set" system virtual table (2000000167)
// works for a permission set declared by the bundle under test's own AL SOURCE (compiled
// fresh by this run, never shipped in a precompiled .app) — the specific gap #2357 left
// half-fixed after the table stopped being unconditionally empty: EnumerateKnownPermissionSets
// in RecordPatches.MetadataPermissionSetVirtualTable.cs originally only ever walked
// precompiled dependency .apps (_bcAppPaths), so a permission set declared only in the
// bundle under test — as Microsoft's own Tests-SINGLESERVER bucket does with
// `permissionset 134611 TestSet` — could never appear here at all.
//
// It also exercises RecordPatches.AggregatePermissionSetVirtualTable.cs's per-row-safe
// drain: BC's own AggregatePermissionSetDataProvider.CreateRecordBuffer is driven ONE
// PermissionSetRecord at a time (not as a single continuous C# iterator over the whole
// union), specifically so a length-overflow throw for one row (a real, if legacy-only,
// case — the System Application ships a Metadata Permission Set role id 22 characters
// long, "System Execute - Basic", wider than the Aggregate table's own Code[20] Role ID
// column) cannot silently truncate every OTHER row's turn, including this bundle's own.
//
// The BEHAVIORAL claim ("Aggregate Permission Set answers this shape on real BC") is
// proven upstream against a live BC service tier — see
// StefanMaron/BusinessCentral.AL.Language.Tests PR for "Test Aggregate Permission Set"
// (60931) / "ALT Agg Perm Set" (60930), per .claude/rules/bc-behavior-tests-go-upstream.md.
// This test exists so a regression in OUR OWN population pipeline fails loudly here,
// without needing the submodule pin bumped first.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class AggregatePermissionSetVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "AggregatePermissionSet");

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
        // WaitForExit(int) returns as soon as the process exits and does NOT wait for the
        // async BeginOutputReadLine/BeginErrorReadLine callbacks to drain -- only the
        // parameterless overload does. Without this the last stdout lines can still be in
        // flight when we read outSb, so an Assert.Contains on a line the runner definitely
        // printed fails intermittently, and more often the more loaded the machine is.
        // That made main red on a DIFFERENT pre-28.0 leg on three consecutive merges.
        // 65 of the 67 subprocess-spawning test files here already do this; these two did not.
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void AggregatePermissionSet_SourceDeclaredPermissionSet_BothTestsPass()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-aps-tests", "cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run (both fixture tests must pass). exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // Positive: the fresh-source-compiled permission set is found, with its
            // declared Caption round-tripping as the row's Name.
            Assert.Contains(
                "PASS  Codeunit60702.AggregatePermissionSet_ThisBundlesDeclaredPermissionSet_IsFound", stdout);
            // Negative: an undeclared role id still fails, not a silent success.
            Assert.Contains(
                "PASS  Codeunit60702.AggregatePermissionSet_GetOnUndeclaredRoleId_Fails", stdout);
            // #2473: the table must NOT snapshot at first touch -- a Tenant Permission Set
            // row inserted after an earlier touch must be visible on a later one, and a
            // subsequently deleted row must not remain a ghost.
            Assert.Contains(
                "PASS  Codeunit60702.AggregatePermissionSet_TenantRowInsertedAfterEarlierTouch_IsVisible", stdout);
            // #2504: redriving on DISPATCH alone is not enough -- a record variable REUSED
            // for a second Get() after an intervening write must see the fresh row too, not
            // just a freshly-declared variable's own first touch.
            Assert.Contains(
                "PASS  Codeunit60702.AggregatePermissionSet_SameRecordVariableReusedAcrossWrite_SeesFreshRow", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
