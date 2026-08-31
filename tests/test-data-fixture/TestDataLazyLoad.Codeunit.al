/// <summary>
/// End-to-end proof for issue #2262: `--test-data` reads a table out of the backup the FIRST
/// TIME anything in the run touches it, and not before — and a table loaded that way survives
/// the codeunit-boundary restore with its backup values intact.
///
/// The subjects are picked so nothing earlier in this bundle, and nothing the dependency
/// install triggers do, has already materialised them, so the first touch really is here in
/// the middle of a test body:
///   - "Country/Region" (table 9) — 139 rows of real CRONUS data, no tableextension rows and
///     no value type this build refuses.
///   - "Shipping Agent" (table 291) — the WRITE subject.
///
/// The body is shared with codeunit 64405 on purpose; see "TDF Lazy Load Steps" for why two
/// symmetric codeunits are what makes the boundary claim independent of the order the runner
/// happens to run them in.
///
/// NOT RUN BY CI — see README.md in this directory.
/// </summary>
codeunit 64404 "Test Data Lazy Load"
{
    Subtype = Test;

    var
        Steps: Codeunit "TDF Lazy Load Steps";

    [Test]
    procedure LazilyLoadedRowsArePristine_ThenThisCodeunitDirtiesThem()
    begin
        Steps.AssertPristineThenDirty();
    end;
}
