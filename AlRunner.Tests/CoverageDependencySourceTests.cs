// CoverageDependencySourceTests — issue #3965.
//
// A statement executed in a sibling SOURCE dependency is dropped from the Cobertura report
// unless that app is also listed as an execution bundle. Program.cs builds the source map from
// `bundles` alone (AlCoverageSourceMap.Build(bundles, ...)), so a dependency compiled from
// source has no root in the map and its statements cannot be attributed.
//
// The statements ARE tracked — measured before the fix by listing the dependency as a second
// bundle, which produces `CdsSubject.Codeunit.al` with a hit on Twice and none on Never. Only
// the attribution is missing, which is why the fix is about which roots reach Build.
//
// Fixture: Fixtures/CoverageDependencySource. `dep` owns TWO procedures and the consuming
// bundle calls exactly one, so a fix that attributes every dependency line indiscriminately
// fails here as loudly as the drop it replaces — the Never() assertion is the control.

using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class CoverageDependencySourceTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "CoverageDependencySource");

    // The executed statement and the never-executed one, read from dep/CdsSubject.Codeunit.al.
    private const int TwiceLine = 9;
    private const int NeverLine = 14;

    private readonly string _scratch;

    public CoverageDependencySourceTests()
    {
        _scratch = TestScratch.Dir("al-runner-coverage-dep-source");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private (string Output, int Exit) Spawn(string coverageOut, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        // Every run must re-emit and re-instrument rather than replay an al-out cache HIT
        // written by a sibling test (.claude/rules/local-test-scope.md).
        args.Append(" --no-cache");
        foreach (var b in bundles) args.Append(" \"").Append(Path.Combine(FixtureRoot, b)).Append('"');
        args.Append(" --coverage --coverage-out \"").Append(coverageOut).Append('"');

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
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

    private static int? HitsFor(XDocument cobertura, string fileNameSuffix, int line)
    {
        var cls = cobertura.Descendants("class").FirstOrDefault(c =>
            c.Attribute("filename")!.Value.Replace('\\', '/').EndsWith(fileNameSuffix, StringComparison.Ordinal));
        if (cls == null) return null;
        var lineEl = cls.Descendants("line").FirstOrDefault(l => (int)l.Attribute("number")! == line);
        return lineEl == null ? null : (int)lineEl.Attribute("hits")!;
    }

    /// <summary>
    /// #3965: the dependency-only run must attribute the executed statement to the dependency's
    /// own source file. The test PASSES either way — the defect is in the report, not the run —
    /// so the assertion is on the hit count and never on the exit code.
    /// </summary>
    [SkippableFact]
    public void DependencyOnlyRun_AttributesTheExecutedStatement_ToTheDependencysSource()
    {
        TestArtifacts.SkipIfMissing();

        var covPath = Path.Combine(_scratch, "dependency-only.xml");
        var (output, exit) = Spawn(covPath, "main");

        // The run itself must succeed: a dropped report on a FAILING run would prove nothing.
        Assert.True(exit == 0, $"the fixture's one test must pass, exit was {exit}.\n{output}");
        Assert.True(File.Exists(covPath), $"no coverage document was written.\n{output}");

        var doc = XDocument.Load(covPath);

        // The consuming bundle is attributed either way; asserted so a fix that broke it is
        // visible here rather than only in the older CoverageTests.
        Assert.NotNull(HitsFor(doc, "main/CdsTests.Codeunit.al", 11));

        var twice = HitsFor(doc, "dep/CdsSubject.Codeunit.al", TwiceLine);
        Assert.True(twice is not null,
            "the dependency's source file is absent from the report although its statement "
            + $"executed (#3965). Files present: {string.Join(", ", doc.Descendants("class").Select(c => c.Attribute("filename")!.Value))}");
        Assert.True(twice >= 1, $"Twice() executed, so line {TwiceLine} must carry a hit; got {twice}");

        // THE control. A fix that attributes every dependency line rather than the executed ones
        // passes the assertion above and fails here.
        var never = HitsFor(doc, "dep/CdsSubject.Codeunit.al", NeverLine);
        Assert.True(never is not null, $"line {NeverLine} must be present as an uncovered statement");
        Assert.True(never == 0,
            $"Never() is not called, so line {NeverLine} must carry no hit; got {never} — the report is "
            + "attributing the dependency's lines rather than its executed statements");
    }

    /// <summary>
    /// The baseline the issue reports as already working: listing the dependency as a second
    /// execution bundle attributes it. Kept because it is what localises the defect to the
    /// source map rather than to the tracker — if this ever fails, the statements are not being
    /// collected at all and #3965's diagnosis is wrong.
    /// </summary>
    [SkippableFact]
    public void ListingTheDependencyAsABundle_AttributesIt_AndIsNotTheFix()
    {
        TestArtifacts.SkipIfMissing();

        var covPath = Path.Combine(_scratch, "with-subject.xml");
        var (output, exit) = Spawn(covPath, "dep", "main");

        Assert.True(exit == 0, $"exit was {exit}.\n{output}");
        var doc = XDocument.Load(covPath);

        Assert.Equal(1, HitsFor(doc, "dep/CdsSubject.Codeunit.al", TwiceLine));
        Assert.Equal(0, HitsFor(doc, "dep/CdsSubject.Codeunit.al", NeverLine));
    }
}
