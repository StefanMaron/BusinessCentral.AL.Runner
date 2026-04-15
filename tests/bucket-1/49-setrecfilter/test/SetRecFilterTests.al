codeunit 55702 "SetRecFilter Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Single-field PK — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure SetRecFilterSinglePKRestrictsCountToOne()
    var
        Rec: Record "SRF Single Table";
    begin
        // [GIVEN] Three records exist
        InsertSingle('AAA', 'Alpha');
        InsertSingle('BBB', 'Beta');
        InsertSingle('CCC', 'Gamma');

        // [WHEN] Get a specific record then SetRecFilter
        Rec.Get('BBB');
        Rec.SetRecFilter();

        // [THEN] Count is restricted to exactly 1 (only the current record)
        Assert.AreEqual(1, Rec.Count(), 'SetRecFilter on single PK should restrict Count to 1');
    end;

    [Test]
    procedure SetRecFilterSinglePKFindSetReturnsOnlyCurrentRecord()
    var
        Rec: Record "SRF Single Table";
        Found: Integer;
    begin
        // [GIVEN] Three records exist
        InsertSingle('D01', 'Delta');
        InsertSingle('D02', 'Epsilon');
        InsertSingle('D03', 'Zeta');

        // [WHEN] Get D02, SetRecFilter, FindSet
        Rec.Get('D02');
        Rec.SetRecFilter();
        Rec.FindSet();

        // [THEN] Only D02 is iterated
        Assert.AreEqual('D02', Rec.Code, 'SetRecFilter should land on the current record');
        Found := 1;
        while Rec.Next() <> 0 do
            Found += 1;
        Assert.AreEqual(1, Found, 'FindSet should iterate exactly 1 record after SetRecFilter');
    end;

    [Test]
    procedure SetRecFilterSinglePKDescription()
    var
        Rec: Record "SRF Single Table";
    begin
        // [GIVEN] A record with a specific description
        InsertSingle('E01', 'Expected Description');
        InsertSingle('E02', 'Other Description');

        // [WHEN] Get E01 and SetRecFilter
        Rec.Get('E01');
        Rec.SetRecFilter();
        Rec.FindFirst();

        // [THEN] The filtered record has the correct description
        Assert.AreEqual('Expected Description', Rec.Description, 'SetRecFilter should return the correct record data');
    end;

    // -----------------------------------------------------------------------
    // Composite PK — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure SetRecFilterCompositePKRestrictsCountToOne()
    var
        Rec: Record "SRF Composite Table";
    begin
        // [GIVEN] Three records that share Doc Type=1 but differ on other PK fields
        InsertComposite(1, 'ORD001', 10000, 'Line 1a');
        InsertComposite(1, 'ORD001', 20000, 'Line 1b');
        InsertComposite(1, 'ORD002', 10000, 'Line 2a');

        // [WHEN] Get a specific composite-PK record and SetRecFilter
        Rec.Get(1, 'ORD001', 10000);
        Rec.SetRecFilter();

        // [THEN] Count restricted to 1 — not 3 (which field-1-only filter would give)
        Assert.AreEqual(1, Rec.Count(), 'SetRecFilter on composite PK should restrict Count to exactly 1');
    end;

    [Test]
    procedure SetRecFilterCompositePKIsolatesCorrectRow()
    var
        Rec: Record "SRF Composite Table";
    begin
        // [GIVEN] Two records sharing first two PK fields
        InsertComposite(2, 'PO001', 100, 'First line');
        InsertComposite(2, 'PO001', 200, 'Second line');

        // [WHEN] Get the second record and SetRecFilter
        Rec.Get(2, 'PO001', 200);
        Rec.SetRecFilter();
        Rec.FindFirst();

        // [THEN] Only the second record is returned
        Assert.AreEqual('Second line', Rec.Description, 'SetRecFilter should isolate the correct composite-PK row');
        Assert.AreEqual(200, Rec."Line No.", 'Filtered record must have Line No.=200');
    end;

    // -----------------------------------------------------------------------
    // Negative: Reset clears the SetRecFilter restriction
    // -----------------------------------------------------------------------

    [Test]
    procedure ResetAfterSetRecFilterRestoresFullCount()
    var
        Rec: Record "SRF Single Table";
    begin
        // [GIVEN] Three records, with SetRecFilter applied
        InsertSingle('R01', 'One');
        InsertSingle('R02', 'Two');
        InsertSingle('R03', 'Three');
        Rec.Get('R01');
        Rec.SetRecFilter();
        Assert.AreEqual(1, Rec.Count(), 'Pre-condition: SetRecFilter gives Count=1');

        // [WHEN] Reset is called
        Rec.Reset();

        // [THEN] Count returns all records, not just 1
        Assert.AreEqual(3, Rec.Count(), 'Reset should clear SetRecFilter and restore full Count');
        Assert.AreNotEqual(1, Rec.Count(), 'Count after Reset must not still be 1');
    end;

    [Test]
    procedure SetRecFilterDoesNotAffectOtherRecordVariables()
    var
        Rec: Record "SRF Single Table";
        Other: Record "SRF Single Table";
    begin
        // [GIVEN] Three records, SetRecFilter on Rec
        InsertSingle('S01', 'One');
        InsertSingle('S02', 'Two');
        InsertSingle('S03', 'Three');
        Rec.Get('S01');
        Rec.SetRecFilter();

        // [THEN] Separate variable sees all 3 records (filters are per-variable)
        Assert.AreEqual(3, Other.Count(), 'SetRecFilter on one variable should not affect another variable');
    end;

    local procedure InsertSingle(Code: Code[20]; Description: Text[100])
    var
        Rec: Record "SRF Single Table";
    begin
        Rec.Init();
        Rec.Code := Code;
        Rec.Description := Description;
        Rec.Insert();
    end;

    local procedure InsertComposite(DocType: Integer; DocNo: Code[20]; LineNo: Integer; Description: Text[100])
    var
        Rec: Record "SRF Composite Table";
    begin
        Rec.Init();
        Rec."Doc Type" := DocType;
        Rec."Doc No." := DocNo;
        Rec."Line No." := LineNo;
        Rec.Description := Description;
        Rec.Insert();
    end;
}
