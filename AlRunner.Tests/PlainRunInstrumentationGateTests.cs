// Issue #2481: a plain run (no --coverage, no --capture-values, no --dap) should pay
// nothing it did not ask for. The 15 PrependStaticCall/PrependProbe/PrependClosedGate/
// PrependFieldUninitGuard sites in NclCecilRewrite.cs turned out, on inspection, to be
// unconditional correctness prepends with no disabled state at all (BLOB-store
// isolation, rowversion stamping, the Date virtual-table window guard, transaction
// commit-point bookkeeping, closed-RecordRef property defaults, NavReport uninit-field
// guards, report-layout hydration) — the same category as ReplaceBodyWithHelper, just
// implemented as a prepend because BC's own body must still run afterward. None of them
// gate on --coverage/--capture-values/--dap, so there is no "off" path to assert zero
// work for.
//
// The actual optional, per-AL-statement/per-scope instrumentation is a SEPARATE, smaller
// set that does not use those four helpers at all: three raw-IL prepends onto
// NavMethodScope.StmtHit/CStmtHit (feeding AlCoverageTracker.OnStmtHit, --coverage,
// #1922) and NavMethodScope.Exit() (feeding AlValueCapture.OnExit, --capture-values,
// second slice of #1640), plus a third prepend onto the same StmtHit/CStmtHit methods
// feeding AlDapSession.OnStmtHit (--dap, #1642). Each callee already gates on a static
// `Enabled` bool as literally its first statement (AlCoverageTracker.cs,
// AlValueCapture.cs, AlDapSession.cs) — this file is the behavioural regression gate the
// issue asks for instead of a timing gate: assert the counter of REAL WORK PERFORMED
// stays zero on a plain run, even though the call site itself fires on every statement.
//
// Deliberately NOT a timing/instructions-retired test — ubuntu-latest has no PMU (see
// the issue), and wall clock moved 60% under load in an unrelated measurement while
// instructions retired moved 0.1%. A counter needs neither: it is exact, on every OS,
// with no noise floor to reason about.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class PlainRunInstrumentationGateTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "CoverageBranch");

    private readonly string _scratch;

    public PlainRunInstrumentationGateTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "al-runner-plainrun-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private (string Output, int Exit) Spawn(params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-cache"); // must actually re-emit/re-instrument, not replay a cache HIT
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        args.Append(" \"").Append(Fixture).Append('"');

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
            Environment = { ["AL_RUNNER_DUMP_INSTRUMENTATION_COUNTERS"] = "1" },
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(240_000), "runner did not exit within 240s");
        p.WaitForExit();
        return (sb.ToString(), p.ExitCode);
    }

    private sealed record Counters(
        long CoverageCallCount, bool CoverageHasRecordedAnyHits,
        long CaptureValuesCallCount, int CaptureValuesCollectedCount,
        long DapCallCount, long DapWorkPerformedCount);

    private static Counters ParseCounters(string output)
    {
        var m = Regex.Match(output,
            @"\[instrumentation-counters\] coverage\.CallCount=(\d+) coverage\.HasRecordedAnyHits=(True|False) " +
            @"captureValues\.CallCount=(\d+) captureValues\.CollectedCount=(\d+) " +
            @"dap\.CallCount=(\d+) dap\.WorkPerformedCount=(\d+)");
        Assert.True(m.Success, $"instrumentation-counters line not found in output:\n{output}");
        return new Counters(
            long.Parse(m.Groups[1].Value), bool.Parse(m.Groups[2].Value),
            long.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value),
            long.Parse(m.Groups[5].Value), long.Parse(m.Groups[6].Value));
    }

    // --- The negative half: a plain run does the call, but zero bookkeeping ------------

    [SkippableFact]
    public void PlainRun_NoOptionalFlags_HooksFireButRecordNoWork()
    {
        TestArtifacts.SkipIfMissing();
        var (output, _) = Spawn(); // no --coverage, no --dap; captureValues has no CLI flag (server-mode only)
        var c = ParseCounters(output);

        // The Cecil-rewritten call sites are unconditional — they MUST fire on every AL
        // statement of this fixture's two test methods (one has an if/else, so both
        // StmtHit and CStmtHit fire). A count of 0 here would mean the hooks never
        // installed at all, a much bigger regression than this issue is about.
        Assert.True(c.CoverageCallCount > 0, $"coverage.CallCount was 0 — StmtHit hook never fired.\n{output}");
        Assert.True(c.CaptureValuesCallCount > 0, $"captureValues.CallCount was 0 — StmtHit/Exit hook never fired.\n{output}");
        Assert.True(c.DapCallCount > 0, $"dap.CallCount was 0 — StmtHit hook never fired.\n{output}");

        // ...but NONE of the three subsystems did any bookkeeping: no coverage hit
        // recorded, no captured-value series entry, no breakpoint/step evaluation. A
        // no-op implementation that always skipped the Enabled check would ALSO show
        // CallCount>0 here, so this half — not the counts above — is the actual claim
        // this gate exists to protect: the callee's early-out is real, not vacuous.
        Assert.False(c.CoverageHasRecordedAnyHits,
            $"a plain run recorded a coverage hit despite --coverage never being requested.\n{output}");
        Assert.Equal(0, c.CaptureValuesCollectedCount);
        Assert.Equal(0, c.DapWorkPerformedCount);
    }

    // --- The positive half: the counters are not just permanently zero -----------------
    //
    // Proven here for --coverage only (the cheapest of the three to enable from the
    // CLI — --capture-values is server-mode-only, see AlValueCaptureSeriesTests.cs /
    // ServerExecuteCapturedValuesSeriesTests for its own enabled-arm proof; --dap needs a
    // live DAP session, see DapServerTests.cs / DapClient for its own enabled-arm proof).
    // What THIS test additionally proves that those don't: enabling --coverage alone
    // does not also flip captureValues/dap's counters — the three gates are independent,
    // not one shared switch.

    [SkippableFact]
    public void CoverageEnabled_RecordsWork_WhileTheOtherTwoStayAtZero()
    {
        TestArtifacts.SkipIfMissing();
        var coveragePath = Path.Combine(_scratch, "cobertura.xml");
        var (output, _) = Spawn("--coverage", $"--coverage-out \"{coveragePath}\"");
        var c = ParseCounters(output);

        Assert.True(c.CoverageCallCount > 0);
        Assert.True(c.CoverageHasRecordedAnyHits,
            $"--coverage was requested but no hit was ever recorded — the counter is dead, not just quiet.\n{output}");

        // captureValues/dap were never requested on this run — their counters must stay
        // exactly as quiet as the fully-plain run above, proving --coverage's own gate
        // does not leak into the other two subsystems' bookkeeping.
        Assert.Equal(0, c.CaptureValuesCollectedCount);
        Assert.Equal(0, c.DapWorkPerformedCount);
    }
}
