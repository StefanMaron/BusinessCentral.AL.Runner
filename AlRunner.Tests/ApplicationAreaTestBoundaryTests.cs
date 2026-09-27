using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #3575: the session's application areas are put back to the
/// value a test codeunit started with after every test method and at the codeunit's end, as BC's
/// <c>NavTestCodeunit.DoRunAsync</c> does. The plain-BC half is pinned upstream (the PR body's
/// <c>Corpus-PR:</c> line); this pins what only the runner decides — that the restore holds under
/// every <c>--isolation</c> mode, since BC's restore does not depend on test isolation, and across
/// a codeunit boundary.
///
/// No Base Application dependency (.claude/rules/no-base-app-in-csharp-tests.md): each AL test
/// raises its own Error() carrying the observed areas, and the runner output is the assertion.
/// </summary>
public class ApplicationAreaTestBoundaryTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle, params string[] extra)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var e in extra) args.Append(" \"").Append(e).Append('"');
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static string WriteBundle(string name)
    {
        var root = TestScratch.Dir(name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b3575000-0000-4000-8000-000000003575",
          "name": "ApplicationAreaTestBoundary3575",
          "publisher": "Repro3575",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 63570, "to": 63579 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "AreaProbe.al"), """
        codeunit 63571 "AAB Setter Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            [Test]
            procedure A_SetsAnArea()
            begin
                ApplicationArea('#Basic,#AABProbe');
                if ApplicationArea() <> '#Basic,#AABProbe' then
                    Error('AAB1 FAIL: set areas read back as [%1]', ApplicationArea());
            end;

            [Test]
            procedure B_NextTestInTheSameCodeunit()
            begin
                if StrPos(ApplicationArea(), '#AABProbe') <> 0 then
                    Error('AAB2 FAIL: next test saw [%1]', ApplicationArea());
            end;

            [Test]
            procedure C_SetsAnAreaAndIsTheLastTest()
            begin
                ApplicationArea('#Basic,#AABLastProbe');
            end;
        }

        codeunit 63572 "AAB Next Codeunit Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            [Test]
            procedure D_NextCodeunit()
            begin
                if StrPos(ApplicationArea(), 'Probe') <> 0 then
                    Error('AAB3 FAIL: next codeunit saw [%1]', ApplicationArea());
            end;
        }
        """);
        return root;
    }

    private static void Has(string output, string s) =>
        Assert.True(output.Contains(s), $"expected [{s}] in runner output:\n{output}");

    private static void Lacks(string output, string s) =>
        Assert.False(output.Contains(s), $"did not expect [{s}] in runner output:\n{output}");

    [SkippableTheory]
    [InlineData("codeunit")]
    [InlineData("test")]
    [InlineData("disabled")]
    public void AreasATestSets_DoNotReachTheNextTestOrCodeunit_UnderEveryIsolation(string isolation)
    {
        TestArtifacts.SkipIfMissing();
        var (output, _) = RunRunner(WriteBundle($"al-runner-apparea-boundary-3575-{isolation}"),
            "--isolation", isolation);

        Has(output, "PASS  Codeunit63571.A_SetsAnArea");
        Has(output, "PASS  Codeunit63571.B_NextTestInTheSameCodeunit");
        Has(output, "PASS  Codeunit63571.C_SetsAnAreaAndIsTheLastTest");
        Has(output, "PASS  Codeunit63572.D_NextCodeunit");
        Lacks(output, "AAB1 FAIL");
        Lacks(output, "AAB2 FAIL");
        Lacks(output, "AAB3 FAIL");
    }
}
