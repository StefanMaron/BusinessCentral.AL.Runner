/// <summary>
/// End-to-end proof for issue #2258: `--test-data` hydrates the in-memory store from a BC
/// backup BEFORE install triggers run, and AL reads the rows back through ordinary Record
/// calls with the values the backup actually holds.
///
/// "No. Series" (table 308, Business Foundation) is the subject on purpose:
///   - it carries 119 rows of real CRONUS setup data, not a token one or two;
///   - none of its fields is a date, time, BLOB or media value, so it is entirely inside
///     the first hydration slice;
///   - its `$ext` companion is empty in the shipped demo database, so no table-extension
///     data is being silently dropped underneath the assertions.
///
/// NOT RUN BY CI — see README.md in this directory.
/// </summary>
codeunit 64400 "Test Data Hydration Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TDF Assert";

    /// <summary>
    /// The positive case, asserting CONCRETE values rather than "some rows exist". An
    /// implementation that inserted 119 blank rows — or that hydrated the wrong table —
    /// fails here.
    /// </summary>
    [Test]
    procedure NoSeries_RowsAreHydratedWithTheirRealValues()
    var
        NoSeries: Record "No. Series";
    begin
        Assert.IsTrue(NoSeries.Get('S-ORD'), 'No. Series S-ORD must exist after --test-data hydration');
        Assert.AreEqual('Sales Order', NoSeries.Description, 'S-ORD Description');
        Assert.AreEqual(true, NoSeries."Default Nos.", 'S-ORD Default Nos.');
        Assert.AreEqual(false, NoSeries."Manual Nos.", 'S-ORD Manual Nos.');
        Assert.AreEqual(false, NoSeries."Date Order", 'S-ORD Date Order');

        // A second row, with a different Description, so a fix that hydrated one row and
        // copied it 119 times would still fail.
        Assert.IsTrue(NoSeries.Get('P-ORD'), 'No. Series P-ORD must exist after --test-data hydration');
        Assert.AreEqual('Purchase Order', NoSeries.Description, 'P-ORD Description');
        Assert.AreEqual(true, NoSeries."Default Nos.", 'P-ORD Default Nos.');
    end;

    /// <summary>
    /// The row COUNT, asserted exactly. A partially-hydrated table is the failure mode the
    /// mechanism's all-or-nothing row building exists to prevent, and only a count can catch
    /// it — Get() on two known codes cannot.
    /// </summary>
    [Test]
    procedure NoSeries_HydratesEveryRowTheBackupHolds()
    var
        NoSeries: Record "No. Series";
    begin
        Assert.AreEqual(119, NoSeries.Count(), 'every No. Series row in the CRONUS backup must be hydrated');
    end;

    /// <summary>
    /// The negative case: a code the backup does NOT contain must still be absent. Without
    /// it, an implementation that inserted a row for every possible key would pass the
    /// positive tests above.
    /// </summary>
    [Test]
    procedure NoSeries_DoesNotInventRowsTheBackupNeverHad()
    var
        NoSeries: Record "No. Series";
    begin
        Assert.IsFalse(NoSeries.Get('NOT-A-REAL-SERIES'), 'hydration must not invent rows');

        // The raising form of Get must still raise for a key the backup never had: a
        // hydration that pre-created every key would satisfy IsFalse above only by accident.
        asserterror NoSeries.Get('NOT-A-REAL-SERIES', true);
    end;

    /// <summary>
    /// Hydration runs BEFORE install triggers, which is the ordering real BC has (the
    /// database with its data exists before any extension is installed) and the ordering the
    /// repo owner specified. The observable consequence is that the rows survive the
    /// per-test restore at every codeunit boundary — they were captured into the install
    /// baseline. A hydration that ran AFTER the capture would read empty from the second
    /// test onwards, so this test passing at all (it is not the first in the codeunit)
    /// already depends on the ordering; asserting a value here makes that explicit.
    /// </summary>
    [Test]
    procedure HydratedRowsSurviveTheCodeunitBoundaryRestore()
    var
        NoSeries: Record "No. Series";
    begin
        Assert.IsTrue(NoSeries.Get('S-ORD'), 'hydrated rows must be part of the captured install baseline');
        Assert.AreEqual('Sales Order', NoSeries.Description, 'S-ORD Description after a baseline restore');
    end;
}
