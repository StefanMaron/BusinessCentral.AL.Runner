// CoverageTests — RED→GREEN guard for --coverage (issue #1922, first slice of #1640).
//
// AlRunner.Tests/Fixtures/CoverageBranch has a known statement layout (see the fixture's
// own AL files): CovProbe.Codeunit.al's `if Flag then ... else ...` gives one TAKEN
// branch (Result := 2) and one guaranteed-UNTAKEN branch (Result := 3) in the same run,
// so the "did not execute" half of coverage cannot pass vacuously — a no-op
// implementation that always reports 0 would fail the positive assertions below, and an
// implementation that always reports 1 would fail the negative ones.
//
// Ghost-test guard: every assertion below names a SPECIFIC AL source line and a SPECIFIC
// hit count (never just "coverage ran without crashing"), and the untaken-branch and
// stack-trace-line-preservation checks assert the actual runtime-observed values are
// EXACTLY what the fixture's source lines say — not merely "cobertura.xml exists".
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class CoverageTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "CoverageBranch");

    private readonly string _scratch;

    public CoverageTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "al-runner-coverage-tests", Guid.NewGuid().ToString("N"));
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
        args.Append(" --no-cache"); // every run below must actually re-emit/re-instrument, not replay an al-out cache HIT from a prior test
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

    /// <summary>Parses a Cobertura <line number=".." hits=".."/> element's hit count for
    /// a given source file (matched by filename suffix) and line number.</summary>
    private static int HitsFor(XDocument cobertura, string fileNameSuffix, int line)
    {
        var cls = cobertura.Descendants("class")
            .Single(c => c.Attribute("filename")!.Value.Replace('\\', '/').EndsWith(fileNameSuffix, StringComparison.Ordinal));
        var lineEl = cls.Descendants("line").Single(l => (int)l.Attribute("number")! == line);
        return (int)lineEl.Attribute("hits")!;
    }

    [SkippableFact]
    public void Coverage_TakenBranchStatement_ReportsNonZeroHit()
    {
        TestArtifacts.SkipIfMissing();
        var coveragePath = Path.Combine(_scratch, "cobertura.xml");
        var (output, exit) = Spawn("--coverage", $"--coverage-out \"{coveragePath}\"");

        Assert.Equal(1, exit); // one test in the fixture deliberately fails
        Assert.True(File.Exists(coveragePath), $"cobertura.xml was not written.\n{output}");
        var doc = XDocument.Load(coveragePath);

        // CovProbe.Codeunit.al line 14 is `Result := 2;` inside `if Flag then` — the test
        // calls Run(true), so this line MUST have executed.
        Assert.Equal(1, HitsFor(doc, "CovProbe.Codeunit.al", 14));
        // Line 13 is the `if Flag then` condition itself (CStmtHit, not StmtHit) — also
        // must have executed exactly because the method was called at all.
        Assert.Equal(1, HitsFor(doc, "CovProbe.Codeunit.al", 13));
    }

    [SkippableFact]
    public void Coverage_UntakenBranchStatement_ReportsZeroHit()
    {
        TestArtifacts.SkipIfMissing();
        var coveragePath = Path.Combine(_scratch, "cobertura.xml");
        var (output, exit) = Spawn("--coverage", $"--coverage-out \"{coveragePath}\"");
        Assert.Equal(1, exit);
        Assert.True(File.Exists(coveragePath), $"cobertura.xml was not written.\n{output}");
        var doc = XDocument.Load(coveragePath);

        // CovProbe.Codeunit.al line 16 is `Result := 3;` — the `else` branch of `if Flag
        // then`. The only call in the fixture is Run(true), so this line NEVER executes.
        // If the implementation always reported 1 (vacuous "coverage"), this fails.
        Assert.Equal(0, HitsFor(doc, "CovProbe.Codeunit.al", 16));

        // Same shape one level up: CovAssert.Codeunit.al line 7 is the Error() call
        // inside `if Expected <> Actual` — the fixture's assertion always passes, so the
        // failure branch inside the assert helper itself never runs either.
        Assert.Equal(0, HitsFor(doc, "CovAssert.Codeunit.al", 7));
        // ...but the condition on line 6 that GUARDS it did run (CStmtHit again) — proves
        // the zero above is a real "didn't take this branch", not "scope never touched".
        Assert.Equal(1, HitsFor(doc, "CovAssert.Codeunit.al", 6));
    }

    [SkippableFact]
    public void Coverage_ReportedLine_MatchesTableTriggerSource()
    {
        // Guards the CLR class-naming fix this issue's investigation surfaced: table
        // trigger scopes are nested in a class named Record<N>, not Table<N>, and would
        // silently vanish from coverage without it (id resolves to 0 -> scope skipped).
        // CoverageBranch has no table, so this reuses RecordTriggerXRec instead — its
        // OnInsert trigger's single statement is documented at AL line 29.
        TestArtifacts.SkipIfMissing();
        var tableFixture = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");
        var coveragePath = Path.Combine(_scratch, "table-cobertura.xml");

        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-cache --coverage --coverage-out \"").Append(coveragePath).Append('"');
        args.Append(" \"").Append(tableFixture).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using (var p = Process.Start(psi)!)
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Assert.True(p.WaitForExit(240_000), "runner did not exit within 240s");
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }

        Assert.True(File.Exists(coveragePath), $"cobertura.xml was not written.\n{sb}");
        var doc = XDocument.Load(coveragePath);
        Assert.Equal(1, HitsFor(doc, "XRecProbe.Table.al", 29));
    }

    [SkippableFact]
    public void Coverage_Disabled_ProducesNoCoberturaFile()
    {
        TestArtifacts.SkipIfMissing();
        var coveragePath = Path.Combine(_scratch, "cobertura.xml");
        // --coverage-out on its own (no --coverage) must not turn coverage on.
        var (_, exit) = Spawn($"--coverage-out \"{coveragePath}\"");
        Assert.Equal(1, exit); // the fixture's deliberate failure still fails the run
        Assert.False(File.Exists(coveragePath),
            "cobertura.xml was written even though --coverage was not passed");
    }

    [SkippableFact]
    public void Coverage_Disabled_TestOutcomesIdenticalToEnabled()
    {
        TestArtifacts.SkipIfMissing();
        var (offOutput, offExit) = Spawn();
        var (onOutput, onExit) = Spawn("--coverage",
            $"--coverage-out \"{Path.Combine(_scratch, "cobertura.xml")}\"");

        Assert.Equal(offExit, onExit);
        Assert.Contains("pass:        1", offOutput);
        Assert.Contains("fail:        1", offOutput);
        Assert.Contains("pass:        1", onOutput);
        Assert.Contains("fail:        1", onOutput);
    }

    /// <summary>
    /// The regression this issue calls out as most likely and least likely to be
    /// noticed: NavMethodScope.StatementNumber backs AlCallStackCapture's "line L" in
    /// every AL stack trace. The Cecil rewrite PREPENDS the coverage hook before
    /// StmtHit/CStmtHit's existing body instead of replacing it, so this must be
    /// byte-identical whether or not --coverage (and therefore the rewrite's hook call)
    /// is exercised.
    /// </summary>
    [SkippableFact]
    public void Coverage_DoesNotChange_AlStackTraceLineNumber()
    {
        TestArtifacts.SkipIfMissing();
        var junitOff = Path.Combine(_scratch, "off.junit.xml");
        var junitOn = Path.Combine(_scratch, "on.junit.xml");

        Spawn($"--output-junit \"{junitOff}\"");
        Spawn("--coverage", $"--coverage-out \"{Path.Combine(_scratch, "on-cobertura.xml")}\"",
            $"--output-junit \"{junitOn}\"");

        Assert.True(File.Exists(junitOff));
        Assert.True(File.Exists(junitOn));

        var failureOff = FailureText(junitOff);
        var failureOn = FailureText(junitOn);

        // Not just "non-null" — the specific line BC's own stack-trace convention would
        // print for a one-statement [Test] procedure body.
        Assert.Contains("DeliberateFailure_ForStackTraceLineRegression line 2", failureOff);
        Assert.Equal(failureOff, failureOn);
    }

    private static string FailureText(string junitPath)
    {
        var doc = XDocument.Load(junitPath);
        var failure = doc.Descendants("testcase")
            .Single(tc => (string)tc.Attribute("name")! == "DeliberateFailure_ForStackTraceLineRegression")
            .Descendants("failure").Single();
        return failure.Value;
    }
}
