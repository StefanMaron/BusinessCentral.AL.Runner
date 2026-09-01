/// <summary>
/// #2273 / #2301 — a table whose backup rows carry a column the base table's own AL field
/// list does not name.
///
/// BC stores a tableextension's fields in the base table itself when the extension is
/// declared in the SAME app as the table it extends: no $ext companion, no "$&lt;app id&gt;"
/// column suffix. Twelve tables used to be refused whole over such a column — `No. Series
/// Line` over `Allow Gaps in Nos.`, `Item` over `Routing No.`, `Purchase Line` over
/// `Prod. Order No.` — which left those tables EMPTY, and an empty table is a state AL
/// reads and believes: ~220 of Microsoft's Tests-SINGLESERVER tests failed with "You cannot
/// assign new numbers from the number series &lt;X&gt;" against a backup whose CONT series has
/// 99,977 numbers left.
///
/// Both assertions below are on a VALUE, not on "the record was found". A test asserting
/// only that `No. Series Line` has rows would pass with the extension column dropped from
/// the row and every extension field left blank, which is the bug's near miss.
/// </summary>
codeunit 64408 "Test Data Same-App Ext Columns"
{
    Subtype = Test;

    var
        Assert: Codeunit "TDF Assert";

    [Test]
    procedure NoSeriesLineHydratesWithItsRange()
    var
        NoSeriesLine: Record "No. Series Line";
    begin
        NoSeriesLine.SetRange("Series Code", 'CONT');
        Assert.IsTrue(NoSeriesLine.FindFirst(), 'CRONUS has a CONT No. Series Line in the backup.');

        Assert.AreEqual('CT000001', NoSeriesLine."Starting No.", 'CONT starting no.');
        Assert.AreEqual('CT100000', NoSeriesLine."Ending No.", 'CONT ending no.');
        Assert.AreEqual('CT000023', NoSeriesLine."Last No. Used", 'CONT last no. used');
    end;

    [Test]
    procedure ANumberCanBeDrawnFromAHydratedSeries()
    var
        NoSeries: Codeunit "No. Series";
        NextNo: Code[20];
    begin
        // The failure this fixture exists for, in the form the MS tests hit it: with the
        // table refused, GetNextNo raised "You cannot assign new numbers from the number
        // series CONT", the error AL raises when a series has no lines at all.
        NextNo := NoSeries.GetNextNo('CONT', WorkDate(), false);

        Assert.AreEqual('CT000024', NextNo, 'the next CONT number follows the hydrated last-used one');
    end;

    [Test]
    procedure ItemHydratesAFieldFromBaseAppsOwnTableExtension()
    var
        Item: Record Item;
    begin
        // "Routing No." is field 99000750, declared by Base Application's own tableextension
        // 99000750 "Mfg. Item" and stored in the Item table itself.
        Assert.IsTrue(Item.Get('SP-BOM2000'), 'CRONUS has item SP-BOM2000 in the backup.');

        Assert.AreEqual('SP-BOM2000', Item."Routing No.", 'the routing no. stored on the item');
    end;

    [Test]
    procedure AnItemWithoutARoutingReadsBlank()
    var
        Item: Record Item;
    begin
        // Negative direction: the merge must not fabricate a value where the backup has
        // none, which a codec defaulting the column would.
        Assert.IsTrue(Item.Get('1896-S'), 'CRONUS has item 1896-S in the backup.');

        Assert.AreEqual('', Item."Routing No.", 'item 1896-S stores no routing no.');
    end;
}
