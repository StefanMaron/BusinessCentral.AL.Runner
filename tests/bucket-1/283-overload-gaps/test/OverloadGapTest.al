codeunit 131002 "OGap Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // ── FindSet(ForUpdate, ForceNewQuery) ────────────────────────────────────

    [Test]
    procedure FindSet_TwoArg_ReturnsTrueWhenRecordExists()
    var
        Src: Codeunit "OGap Source";
        Rec: Record "OGap Table";
    begin
        // [GIVEN] One record in the table
        Rec.Init();
        Rec."No." := 'F1';
        Rec.Insert();

        // [WHEN] FindSet(true, false) — ForUpdate=true, ForceNewQuery=false
        // [THEN] Returns true (record found)
        Assert.IsTrue(Src.FindSet_TwoArg(Rec), 'FindSet(true,false) must return true when records exist');
    end;

    [Test]
    procedure FindSet_TwoArg_ReturnsFalseWhenEmpty()
    var
        Src: Codeunit "OGap Source";
        Rec: Record "OGap Table";
    begin
        // [GIVEN] No records matching the filter
        Rec.SetFilter("No.", 'NEVEREXISTS');

        // [WHEN/THEN] FindSet(true, false) returns false on empty set
        Assert.IsFalse(Src.FindSet_TwoArg(Rec), 'FindSet(true,false) must return false when no records match');
    end;

    // ── Insert(RunTrigger, CheckMandatoryFields) ─────────────────────────────

    [Test]
    procedure Insert_TwoArg_InsertsRecord()
    var
        Src: Codeunit "OGap Source";
        Rec: Record "OGap Table";
    begin
        // [GIVEN] A new record
        Rec.Init();
        Rec."No." := 'I1';

        // [WHEN] Insert(true, true)
        Src.Insert_TwoArg(Rec);

        // [THEN] Record exists in table
        Rec.Reset();
        Rec.SetFilter("No.", 'I1');
        Assert.IsTrue(Rec.FindFirst(), 'Insert(true,true) must persist the record');
    end;

    [Test]
    procedure Insert_TwoArg_DuplicateErrors()
    var
        Src: Codeunit "OGap Source";
        Rec: Record "OGap Table";
    begin
        // [GIVEN] A record already inserted
        Rec.Init();
        Rec."No." := 'DUP';
        Rec.Insert();

        // [WHEN] Insert the same key again with Insert(true, true)
        // [THEN] Error — duplicate primary key
        asserterror Src.Insert_TwoArg(Rec);
        Assert.ExpectedError('already exists');
    end;
}
