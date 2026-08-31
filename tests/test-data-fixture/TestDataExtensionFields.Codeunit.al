/// <summary>
/// End-to-end proof for issue #2261: `--test-data` merges a table's `$ext` companion into
/// the base record, so AL reads back fields contributed by a tableextension.
///
/// EVERY ASSERTION HERE IS ON AN EXTENSION FIELD'S VALUE, DELIBERATELY.
/// The reader's CLI accepts `--mergeExtensions` (camelCase), ignores it, and exits 0 —
/// measured, not assumed. A test asserting "the table hydrated" or "the record was found"
/// would pass with the merge not happening at all: `Source Code Setup` would come back with
/// its ONE own field ("Primary Key") and about fifty blanks, and `Get` would still succeed.
/// So the claim has to be a value that exists only because the join ran.
///
/// The two tables are the ones behind the 15 measured failures in #2240:
///   - `Source Code Setup` (242, Business Foundation) — one own field, ~50 from Base
///     Application's `SourceCodeSetupExt`.
///   - `Purchases & Payables Setup` (312, Base Application) — "Invoice Nos." is an extension
///     field, and it is exactly the value behind "Invoice Nos. must have a value in
///     Purchases & Payables Setup".
///
/// NOT RUN BY CI — see README.md in this directory.
/// </summary>
codeunit 64402 "Test Data Extension Fields"
{
    Subtype = Test;

    var
        Assert: Codeunit "TDF Assert";

    /// <summary>
    /// The positive case. "Primary Key" is the table's only own field; every other assertion
    /// below reads a field that lives in `Source Code Setup$ext` and can only have a value
    /// if the merge ran.
    /// </summary>
    [Test]
    procedure SourceCodeSetup_ExtensionFieldsCarryTheirBackupValues()
    var
        SourceCodeSetup: Record "Source Code Setup";
    begin
        Assert.IsTrue(SourceCodeSetup.Get(), 'Source Code Setup must exist after --test-data hydration');

        // The one field stored in the base table — hydrated before #2261 too.
        Assert.AreEqual('', SourceCodeSetup."Primary Key", 'Source Code Setup Primary Key');

        // Extension fields. Blank before #2261 because the whole table was skipped, and blank
        // again if --merge-extensions is ever misspelled into the silently-ignored form.
        Assert.AreEqual('SALES', SourceCodeSetup.Sales, 'Source Code Setup Sales');
        Assert.AreEqual('PURCHASES', SourceCodeSetup.Purchases, 'Source Code Setup Purchases');
        Assert.AreEqual('GENJNL', SourceCodeSetup."General Journal", 'Source Code Setup General Journal');
        Assert.AreEqual('SALESJNL', SourceCodeSetup."Sales Journal", 'Source Code Setup Sales Journal');
        Assert.AreEqual('PURCHJNL', SourceCodeSetup."Purchase Journal", 'Source Code Setup Purchase Journal');
    end;

    /// <summary>
    /// The negative direction on the same record: extension fields the CRONUS backup leaves
    /// empty must still read empty. Without this, a "merge" that filled every extension field
    /// with the same non-blank value would pass the test above.
    /// </summary>
    [Test]
    procedure SourceCodeSetup_ExtensionFieldsTheBackupLeavesEmptyStayEmpty()
    var
        SourceCodeSetup: Record "Source Code Setup";
    begin
        SourceCodeSetup.Get();
        Assert.AreEqual('', SourceCodeSetup."Post Recognition", 'Source Code Setup Post Recognition is blank in CRONUS');
        Assert.AreEqual('', SourceCodeSetup."Post Value", 'Source Code Setup Post Value is blank in CRONUS');
        // ...while a neighbouring field in the same tableextension is not, so "everything is
        // blank" cannot satisfy both assertions.
        Assert.AreEqual('CLSINCOME', SourceCodeSetup."Close Income Statement", 'Source Code Setup Close Income Statement');
    end;

    // NOT ASSERTED HERE, AND SAID OUT LOUD: `Purchases & Payables Setup` (312) is the other
    // table behind the #2240 failures, and its "Invoice Nos." IS an extension field with the
    // value 'P-INV' in this backup — the merge resolves it correctly. The table still does not
    // hydrate, because it also carries "Allow Document Deletion Before", a Date, and this
    // runner build refuses a whole table rather than rebuild an AL Date from a SQL value no
    // service tier has adjudicated. That is #2259, not #2261. A test asserting P-INV here
    // would be red for a reason this change cannot fix; see the PR body for the measurement.

    /// <summary>
    /// A table with NO extension data must be unaffected by the merge. "No. Series" has an
    /// empty `$ext` companion, so its values must be exactly what they were before #2261 —
    /// this is the regression half: turning the merge on must not disturb the 289 tables the
    /// first slice already hydrated.
    /// </summary>
    [Test]
    procedure ATableWithoutExtensionData_IsUnchangedByTheMerge()
    var
        NoSeries: Record "No. Series";
    begin
        Assert.IsTrue(NoSeries.Get('S-ORD'), 'No. Series S-ORD must still exist with --merge-extensions on');
        Assert.AreEqual('Sales Order', NoSeries.Description, 'S-ORD Description');
        Assert.AreEqual(119, NoSeries.Count(), 'every No. Series row must still be hydrated');
    end;
}
