codeunit 313400 "PSTRA Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure AddBufferLine(var TempBuf: Record "PSTRA Demo Tbl" temporary; TemplID: Code[20]; SetID: Integer; LineNo: Integer; AttrID: Integer; Descr: Text[50])
    begin
        TempBuf.Init();
        TempBuf."Template ID" := TemplID;
        TempBuf."Set ID" := SetID;
        TempBuf."Line No." := LineNo;
        TempBuf."Attribute ID" := AttrID;
        TempBuf.Description := Descr;
        TempBuf.Insert();
    end;

    [Test]
    procedure SetConditions_AllValidLines_PreservesAll()
    var
        Tmp: Record "PSTRA Demo Tbl" temporary;
        ListPart: Page "PSTRA Demo ListPart";
    begin
        // Three rows all with non-zero Attribute ID — all should survive the filter
        AddBufferLine(Tmp, 'TEMPL1', 1, 10000, 5, 'Wood');
        AddBufferLine(Tmp, 'TEMPL1', 1, 20000, 10, '100');
        AddBufferLine(Tmp, 'TEMPL1', 1, 30000, 15, 'Red');

        ListPart.SetConditions(Tmp);

        Assert.AreEqual(3, ListPart.CountRows(), 'Page must contain all 3 valid rows');
    end;

    [Test]
    procedure SetConditions_FiltersEmptyLines()
    var
        Tmp: Record "PSTRA Demo Tbl" temporary;
        ListPart: Page "PSTRA Demo ListPart";
    begin
        // Middle row has Attribute ID = 0 and should be excluded by the filter
        AddBufferLine(Tmp, 'TEMPL1', 1, 10000, 5, 'Wood');
        AddBufferLine(Tmp, 'TEMPL1', 1, 20000, 0, '');
        AddBufferLine(Tmp, 'TEMPL1', 1, 30000, 10, '100');

        ListPart.SetConditions(Tmp);

        Assert.AreEqual(2, ListPart.CountRows(), 'Page must contain only the 2 non-empty rows');
    end;

    [Test]
    procedure SetConditions_EmptySource_YieldsZeroRows()
    var
        Tmp: Record "PSTRA Demo Tbl" temporary;
        ListPart: Page "PSTRA Demo ListPart";
    begin
        // No rows in source → page must have zero rows
        ListPart.SetConditions(Tmp);

        Assert.AreEqual(0, ListPart.CountRows(), 'Page must contain zero rows for empty source');
    end;

    [Test]
    procedure SetConditions_AllFilteredOut_YieldsZeroRows()
    var
        Tmp: Record "PSTRA Demo Tbl" temporary;
        ListPart: Page "PSTRA Demo ListPart";
    begin
        // All rows have Attribute ID = 0 — all filtered out
        AddBufferLine(Tmp, 'TEMPL1', 1, 10000, 0, 'Zero');
        AddBufferLine(Tmp, 'TEMPL1', 1, 20000, 0, 'ZeroTwo');

        ListPart.SetConditions(Tmp);

        Assert.AreEqual(0, ListPart.CountRows(), 'Page must contain zero rows when all are filtered out');
    end;

    [Test]
    procedure TwoPageInstances_HaveSeparateStores()
    // This test exposes the SourceTableTemporary=true bug:
    // if the page Rec is backed by the shared global table (isTemporary=false),
    // PageB.SetConditions deletes PageA's rows (via DeleteAll on the shared table),
    // so PageA.CountRows() would return 0 instead of 3.
    var
        TmpA: Record "PSTRA Demo Tbl" temporary;
        TmpB: Record "PSTRA Demo Tbl" temporary;
        PageA: Page "PSTRA Demo ListPart";
        PageB: Page "PSTRA Demo ListPart";
    begin
        // PageA: 3 rows with unique PKs
        AddBufferLine(TmpA, 'TEMPL-A', 1, 10000, 5, 'Wood');
        AddBufferLine(TmpA, 'TEMPL-A', 1, 20000, 10, 'Steel');
        AddBufferLine(TmpA, 'TEMPL-A', 1, 30000, 15, 'Iron');

        // PageB: 1 row (different Template ID)
        AddBufferLine(TmpB, 'TEMPL-B', 2, 10000, 7, 'Copper');

        PageA.SetConditions(TmpA);
        PageB.SetConditions(TmpB);

        // If Rec is NOT properly temporary, PageB.SetConditions (which calls DeleteAll)
        // wipes PageA's rows from the shared store. PageA would then return 0.
        Assert.AreEqual(3, PageA.CountRows(), 'PageA must retain its 3 rows after PageB is populated');
        Assert.AreEqual(1, PageB.CountRows(), 'PageB must have exactly 1 row');
    end;

    [Test]
    procedure SetConditions_CalledTwice_SecondCallUpdates()
    var
        Tmp1: Record "PSTRA Demo Tbl" temporary;
        Tmp2: Record "PSTRA Demo Tbl" temporary;
        ListPart: Page "PSTRA Demo ListPart";
    begin
        // First call: 3 rows
        AddBufferLine(Tmp1, 'TEMPL1', 1, 10000, 5, 'Wood');
        AddBufferLine(Tmp1, 'TEMPL1', 1, 20000, 10, 'Steel');
        AddBufferLine(Tmp1, 'TEMPL1', 1, 30000, 15, 'Iron');
        ListPart.SetConditions(Tmp1);
        Assert.AreEqual(3, ListPart.CountRows(), 'After first call: 3 rows expected');

        // Second call: 1 row — DeleteAll() must clear previous rows, then insert new
        AddBufferLine(Tmp2, 'TEMPL1', 1, 10000, 5, 'Brass');
        ListPart.SetConditions(Tmp2);
        Assert.AreEqual(1, ListPart.CountRows(), 'After second call: 1 row expected');
    end;
}
