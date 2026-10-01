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

    /// <summary>The CLI's "no <paramref name="s"/> in the output", for a string only a failure message can carry.</summary>
    private static void Lacks(ServerRunResult r, string s) =>
        Assert.False(r.Tests.Any(t => t.Message.Contains(s)) || r.CompilationErrors.Any(c => c.Contains(s)),
            $"did not expect [{s}] in runner output:\n{r}");

    [SkippableTheory]
    [InlineData("codeunit")]
    [InlineData("test")]
    [InlineData("disabled")]
    public async Task AreasATestSets_DoNotReachTheNextTestOrCodeunit_UnderEveryIsolation(string isolation)
    {
        TestArtifacts.SkipIfMissing();
        var r = await SuiteServer.RunViaServer(new[] { WriteBundle($"al-runner-apparea-boundary-3575-{isolation}") },
            Array.Empty<string>(), testIsolation: isolation);

        r.AssertPassed("Codeunit63571.A_SetsAnArea");
        r.AssertPassed("Codeunit63571.B_NextTestInTheSameCodeunit");
        r.AssertPassed("Codeunit63571.C_SetsAnAreaAndIsTheLastTest");
        r.AssertPassed("Codeunit63572.D_NextCodeunit");
        Lacks(r, "AAB1 FAIL");
        Lacks(r, "AAB2 FAIL");
        Lacks(r, "AAB3 FAIL");
    }
}
