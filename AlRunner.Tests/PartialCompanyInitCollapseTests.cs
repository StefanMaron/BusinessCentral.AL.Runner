// PartialCompanyInitCollapseTests — issue #3561, the two arms that have no end-to-end route.
//
// Everything else about accept-partial-company-init is driven through a real runner process
// (PartialCompanyInitAcceptanceTests, PartialCompanyInitAcceptanceEscalationTests). These two
// cannot be: CompanyInitializer only ever records Base App's codeunit 2, and acceptance is keyed
// on the codeunit, so a single run cannot produce two aborts that differ in the way each arm
// needs — one accepted and one not, or two messages under one codeunit. Driving the two helpers
// the run itself calls (Reporter.FinalizeCompanyInitFailures at both drain sites,
// Reporter.UnacceptedCompanyInitFailures at the escalation) is the closest reachable statement,
// and it is the mechanism rather than a re-statement of the exit-code wiring the process-level
// arms already pin.
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class PartialCompanyInitCollapseTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("al-runner-cip-collapse-unit-");

    public PartialCompanyInitCollapseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private ExpectationManifest AcceptingCodeunit2()
    {
        File.WriteAllText(Path.Combine(_dir, "accept-company-init.json"),
            """
            [{ "codeunitId": 2, "CodeunitName": "Company-Initialize", "Method": "*",
               "Mode": "accept-partial-company-init",
               "Reason": "ships without the dependency codeunit 2 needs" }]
            """);
        return ExpectationManifest.LoadFromDirectory(_dir);
    }

    private static CompanyInitFailure Abort(int id, string name, string message)
        => new(id, name, "InvalidOperationException", message);

    private static BucketResult Bucket(params CompanyInitFailure[] failures)
        => new("/b", BucketStage.Ran, Array.Empty<string>(), null, Array.Empty<TestResult>(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, CompanyInitFailures: failures);

    /// <summary>
    /// The key is the whole of what the accumulator records, so two aborts of the SAME codeunit
    /// that failed at different points stay two records. Collapsing on the codeunit alone would
    /// report one of the two causes and silently drop the other — the opposite defect to the
    /// duplication #3561 removed, and the more expensive one, since the dropped line is the one
    /// nobody knows to look for.
    /// </summary>
    [Fact]
    public void TwoAbortsOfOneCodeunit_WithDifferentMessages_DoNotCollapse()
    {
        var finalized = Reporter.FinalizeCompanyInitFailures(
            new[]
            {
                Abort(2, "Company-Initialize", "InitSourceCodeSetup did not finish"),
                Abort(2, "Company-Initialize", "InitSourceCodeSetup did not finish"),
                Abort(2, "Company-Initialize", "InsertMarketingSetup did not finish"),
            },
            manifest: null);

        Assert.Equal(2, finalized.Count);
        var first = Assert.Single(finalized, f => f.Message.StartsWith("InitSourceCodeSetup"));
        var second = Assert.Single(finalized, f => f.Message.StartsWith("InsertMarketingSetup"));
        // Only the two IDENTICAL records folded, and the fold carries the count.
        Assert.Equal(2, first.Count);
        Assert.Equal(1, second.Count);
        Assert.Contains("×2 app group(s)", Reporter.DescribeCompanyInitFailure(first));
        Assert.DoesNotContain("app group(s)", Reporter.DescribeCompanyInitFailure(second));
    }

    /// <summary>
    /// The acceptance is per codeunit, so a run carrying an accepted abort AND one of a codeunit
    /// no entry names is still not clean: the escalation reads
    /// <see cref="Reporter.UnacceptedCompanyInitFailures"/>, which keeps exactly the second one.
    /// A "any acceptance accepts the run" implementation — the shortcut this mode is a targeted
    /// alternative to — passes every other arm in this suite and fails here.
    /// </summary>
    [Fact]
    public void AnAcceptedAbortAlongsideAnUnacceptedOne_LeavesTheRunUnclean()
    {
        var manifest = AcceptingCodeunit2();
        var finalized = Reporter.FinalizeCompanyInitFailures(
            new[]
            {
                Abort(2, "Company-Initialize", "InitSourceCodeSetup did not finish"),
                Abort(9999, "Some Other Initialize", "the other one did not finish either"),
            },
            manifest);

        // Both are still REPORTED — acceptance suppresses the escalation, never the record.
        Assert.Equal(2, finalized.Count);
        Assert.Equal("ships without the dependency codeunit 2 needs",
            Assert.Single(finalized, f => f.CodeunitId == 2).AcceptedReason);
        Assert.Null(Assert.Single(finalized, f => f.CodeunitId == 9999).AcceptedReason);

        var unaccepted = Reporter.UnacceptedCompanyInitFailures(new[] { Bucket(finalized.ToArray()) });
        Assert.Equal(9999, Assert.Single(unaccepted).CodeunitId);
    }

    /// <summary>
    /// The control for the arm above: with only the accepted abort, nothing is left for the
    /// escalation to read, which is the whole of what the mode buys.
    /// </summary>
    [Fact]
    public void AnAcceptedAbortAlone_LeavesNothingForTheEscalation()
    {
        var finalized = Reporter.FinalizeCompanyInitFailures(
            new[] { Abort(2, "Company-Initialize", "InitSourceCodeSetup did not finish") },
            AcceptingCodeunit2());

        Assert.Single(finalized);
        Assert.Empty(Reporter.UnacceptedCompanyInitFailures(new[] { Bucket(finalized.ToArray()) }));
    }
}
