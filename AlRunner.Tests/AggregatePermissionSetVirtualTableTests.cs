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
using Xunit;

namespace AlRunner.Tests;

public sealed class AggregatePermissionSetVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "AggregatePermissionSet");

    [Fact]
    public async Task AggregatePermissionSet_SourceDeclaredPermissionSet_BothTestsPass()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"expected a clean run (both fixture tests must pass). exit={r.ExitCode}\n{r}");

        // Positive: the fresh-source-compiled permission set is found, with its
        // declared Caption round-tripping as the row's Name.
        r.AssertPassed("Codeunit60702.AggregatePermissionSet_ThisBundlesDeclaredPermissionSet_IsFound");
        // Negative: an undeclared role id still fails, not a silent success.
        r.AssertPassed("Codeunit60702.AggregatePermissionSet_GetOnUndeclaredRoleId_Fails");
        // #2473: the table must NOT snapshot at first touch -- a Tenant Permission Set
        // row inserted after an earlier touch must be visible on a later one, and a
        // subsequently deleted row must not remain a ghost.
        r.AssertPassed("Codeunit60702.AggregatePermissionSet_TenantRowInsertedAfterEarlierTouch_IsVisible");
        // #2504: redriving on DISPATCH alone is not enough -- a record variable REUSED
        // for a second Get() after an intervening write must see the fresh row too, not
        // just a freshly-declared variable's own first touch.
        r.AssertPassed("Codeunit60702.AggregatePermissionSet_SameRecordVariableReusedAcrossWrite_SeesFreshRow");
        r.AssertNoFailures();
    }
}
