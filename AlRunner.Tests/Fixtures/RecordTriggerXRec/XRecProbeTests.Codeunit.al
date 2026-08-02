/// <summary>
/// Regression proof that inserting a record whose OnInsert trigger reads xRec
/// does not throw an InvalidCastException.
///
/// Before the fix, NavRecord.OldRecord built the xRec before-image as a base
/// NavRecord (because the runner forces NCLMetaApplicationObject's
/// ApplicationObjectConstructor to null, so NCLMetaTable.CreateObjectInstance
/// fell into its `new NavRecord(...)` fallback). The compiled xRec accessor then
/// cast that base NavRecord to the concrete Record60100 and threw
///   InvalidCastException: Unable to cast 'NavRecord' to 'Record60100'.
/// The fix makes CreateObjectInstance build the owner's concrete CLR type.
/// </summary>
codeunit 60110 "xRec Probe Tests RXT"
{
    Subtype = Test;

    var
        Assert: Codeunit "xRec Assert RXT";

    [Test]
    procedure Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage()
    var
        Rec: Record "xRec Probe RXT";
    begin
        // [GIVEN] a fresh record
        Rec.Init();
        Rec."No." := 'A1';

        // [WHEN] it is inserted with its OnInsert trigger running (which reads xRec)
        Rec.Insert(true);

        // [THEN] the trigger ran and read xRec's before-image (Counter 0 -> 1).
        // If OldRecord were a base NavRecord, the xRec cast would have thrown
        // before this point.
        Rec.Get('A1');
        Assert.AreEqual('1', Format(Rec."Counter"),
            'OnInsert should have read xRec (before-image Counter = 0) and set Counter = 1');
    end;
}
