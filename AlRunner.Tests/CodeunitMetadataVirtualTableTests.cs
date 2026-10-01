// CodeunitMetadataVirtualTableTests — issue #2544.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that OUR
// OWN population of the "CodeUnit Metadata" system virtual table (2000000137) works for
// codeunits declared by the bundle under test's own AL SOURCE, compiled fresh by this run
// and never shipped in a precompiled .app.
//
// Before the fix, table 2000000137 had no managed provider, so RecordPatches'
// GetDataAccessForTableCore fell through to the plain in-memory temp store and every read
// answered zero rows: Get() silently returned false and FindSet() raised. That made it the
// last missing member of a family the runner already implements — Table Metadata
// (2000000136), Page Metadata (2000000138), Report Metadata (2000000139).
//
// The fixture is shaped so a provider that answered every Get with a FIXED or BLANK row
// would fail: "CMV Bound" declares TableNo and nothing else, "CMV Single" declares
// SingleInstance and no TableNo, and the test codeunit itself declares Subtype = Test — so
// each of the three columns is asserted against a codeunit whose declaration makes it
// different from the others. The two negative tests (an unused id, and a filter selecting
// nothing) close the remaining hole.
//
// The BEHAVIORAL claim ("CodeUnit Metadata answers this shape on real BC") is proven
// upstream against a live BC service tier by "Test Codeunit Metadata Virt T" (60962) in
// StefanMaron/BusinessCentral.AL.Language.Tests, per
// .claude/rules/bc-behavior-tests-go-upstream.md. This test exists so a regression in OUR
// OWN population pipeline fails loudly here, without needing the submodule pin bumped first.
using Xunit;

namespace AlRunner.Tests;

public sealed class CodeunitMetadataVirtualTableTests
{
    /// <summary>The cap this file's subprocess spawns actually apply, and the single source of
    /// the figure their timeout messages report (#4275). Derived rather than repeated: a literal
    /// in the message is invisible while it happens to match, and wrong the moment the cap moves.
    /// Measured for real on #3435 — a cap squeezed to 3s still threw "did not exit within 120s".
    /// Same shape as BcVersionDefaultDocumentationTests.SpawnTimeoutMs (#3487).</summary>
    private const int SpawnTimeoutMs = 120_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "CodeunitMetadataVirtualTable");

    [Fact]
    public async Task CodeunitMetadata_SourceCompiledCodeunits_AllFixtureTestsPass()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"expected a clean run (every fixture test must pass). exit={r.ExitCode}\n{r}");

        // Positive: a fresh-source-compiled codeunit is found, and TableNo /
        // SingleInstance / Subtype are read off ITS OWN declaration.
        r.AssertPassed("Codeunit60764.CodeunitMetadata_SourceCompiledCodeunit_ColumnsComeFromItsDeclaration");
        // The mirror declaration: SingleInstance true, TableNo absent. Together with the
        // one above, this is what rules out a fixed row satisfying both.
        r.AssertPassed("Codeunit60764.CodeunitMetadata_SingleInstanceCodeunit_ReportsTrueAndNoTableNo");
        // Subtype is an OPTION column resolved against the live metatable's own option
        // string, not a hardcoded ordinal table.
        r.AssertPassed("Codeunit60764.CodeunitMetadata_TestCodeunit_ReportsSubtypeTest");
        // #3536: a quoted Subtype identifier is the same declaration as the bare one...
        r.AssertPassed("Codeunit60764.CodeunitMetadata_QuotedSubtype_IsReadAsTheIdentifierItIs");
        // ...and a row the resolver cannot answer may not take the rest of the table with
        // it. This one asserts the containment through the runner's real populate path,
        // which is where the whole-table abort lived.
        r.AssertPassed("Codeunit60764.CodeunitMetadata_QuotedSubtypeCodeunit_DoesNotSuppressTheOtherRows");
        // Negative: an id no codeunit uses still answers false, not a silent success.
        r.AssertPassed("Codeunit60764.CodeunitMetadata_UnknownCodeunitId_ReturnsFalse");
        // Negative: filtering discriminates — one row for a real id, none for an unused
        // one. A provider inserting one blank row would pass Get() and fail this.
        r.AssertPassed("Codeunit60764.CodeunitMetadata_FilterOnId_DiscriminatesBetweenRows");
        r.AssertNoFailures();
    }
}
