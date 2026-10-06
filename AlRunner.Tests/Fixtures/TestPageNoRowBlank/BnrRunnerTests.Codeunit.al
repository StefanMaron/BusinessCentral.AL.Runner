// Runner-mechanism fixture for issue #5358. The AL arms mirror the BC claim measured by corpus
// codeunit 69940 "BNR Blank Part Tests" (StefanMaron/BusinessCentral.AL.Language.Tests): a control
// of a part or page that shows no row reads blank, whatever its field type.
codeunit 73400 "BNR Runner Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "BNR Assert";

    local procedure Initialize()
    var
        Header: Record "BNR Header";
        Line: Record "BNR Line";
    begin
        Line.DeleteAll();
        Header.DeleteAll();
        Header.Init();
        Header."No." := 'H0';
        Header.Insert();
        Header.Init();
        Header."No." := 'H1';
        Header.Insert();
    end;

    local procedure InsertFullLine(HeaderNo: Code[20]; LineNo: Integer)
    var
        Line: Record "BNR Line";
    begin
        Line.Init();
        Line."Header No." := HeaderNo;
        Line."Line No." := LineNo;
        Line.QTxt := 'text';
        Line.QCd := 'CODE';
        Line.QInt := 42;
        Line.QDec := 3.25;
        Line.QBool := true;
        Line.QOpt := Line.QOpt::Gamma;
        Line.QDt := DMY2Date(2, 3, 2024);
        Line.QTm := 123456T;
        Line.QDtTm := CreateDateTime(DMY2Date(2, 3, 2024), 123456T);
        Line.QEn := Line.QEn::One;
        Line.QBig := 9000000000L;
        Line.QGd := '{11111111-2222-3333-4444-555555555555}';
        Line.QDur := 5000;
        Line.QIntInit := 7;
        Line.QDecInit := 7.5;
        Line.QBoolInit := false;
        Line.Insert();
    end;

    [Test]
    procedure NoRow_Part_EveryControlReadsBlank()
    var
        Card: TestPage "BNR Card";
    begin
        Initialize();
        InsertFullLine('H1', 10);
        Card.OpenView();
        Card.GoToKey('H0');
        Assert.AreEqual('', Card.Lines.HeaderNo.Value, 'HeaderNo');
        Assert.AreEqual('', Card.Lines.LineNo.Value, 'LineNo');
        Assert.AreEqual('', Card.Lines.QTxt.Value, 'QTxt');
        Assert.AreEqual('', Card.Lines.QCd.Value, 'QCd');
        Assert.AreEqual('', Card.Lines.QInt.Value, 'QInt');
        Assert.AreEqual('', Card.Lines.QDec.Value, 'QDec');
        Assert.AreEqual('', Card.Lines.QBool.Value, 'QBool');
        Assert.AreEqual('', Card.Lines.QOpt.Value, 'QOpt');
        Assert.AreEqual('', Card.Lines.QDt.Value, 'QDt');
        Assert.AreEqual('', Card.Lines.QTm.Value, 'QTm');
        Assert.AreEqual('', Card.Lines.QDtTm.Value, 'QDtTm');
        Assert.AreEqual('', Card.Lines.QEn.Value, 'QEn');
        Assert.AreEqual('', Card.Lines.QBig.Value, 'QBig');
        Assert.AreEqual('', Card.Lines.QGd.Value, 'QGd');
        Assert.AreEqual('', Card.Lines.QDur.Value, 'QDur');
        Assert.AreEqual('', Card.Lines.QIntInit.Value, 'QIntInit');
        Assert.AreEqual('', Card.Lines.QDecInit.Value, 'QDecInit');
        Assert.AreEqual('', Card.Lines.QBoolInit.Value, 'QBoolInit');
    end;

    [Test]
    procedure NoRow_Part_TypedReadsAreTheTypeDefault()
    var
        Card: TestPage "BNR Card";
    begin
        Initialize();
        InsertFullLine('H1', 10);
        Card.OpenView();
        Card.GoToKey('H0');
        Assert.AreEqual(0, Card.Lines.QInt.AsInteger(), 'AsInteger');
        Assert.AreEqual(0, Card.Lines.QDec.AsDecimal(), 'AsDecimal');
        Assert.IsFalse(Card.Lines.QBool.AsBoolean(), 'AsBoolean');
        Assert.AreEqual(0D, Card.Lines.QDt.AsDate(), 'AsDate');
        Assert.AreEqual(0T, Card.Lines.QTm.AsTime(), 'AsTime');
        Assert.AreEqual(0, Card.Lines.QIntInit.AsInteger(), 'AsInteger of an InitValue field');
        Assert.AreEqual(0, Card.Lines.QDecInit.AsDecimal(), 'AsDecimal of an InitValue field');
        Assert.IsFalse(Card.Lines.QBoolInit.AsBoolean(), 'AsBoolean of an InitValue field');
    end;

    [Test]
    procedure PartShowingARow_ReadsItsValues_AndBlanksOnlyWhileTheHostShowsNoLines()
    var
        Card: TestPage "BNR Card";
    begin
        Initialize();
        InsertFullLine('H1', 10);
        Card.OpenView();
        Card.GoToKey('H1');
        Assert.AreEqual('42', Card.Lines.QInt.Value, 'a row is shown');
        Assert.AreEqual('7', Card.Lines.QIntInit.Value, 'a row is shown (InitValue field)');
        Assert.AreEqual(42, Card.Lines.QInt.AsInteger(), 'typed read of a shown row');

        Card.GoToKey('H0');
        Assert.AreEqual('', Card.Lines.QInt.Value, 'no row is shown');

        Card.GoToKey('H1');
        Assert.AreEqual('42', Card.Lines.QInt.Value, 'a row is shown again');
    end;

    [Test]
    procedure NoRow_PartOfAHostOverAnEmptyTable_EveryControlReadsBlank()
    var
        Card: TestPage "BNR Card";
        Header: Record "BNR Header";
        Line: Record "BNR Line";
    begin
        Initialize();
        Header.DeleteAll();
        Line.DeleteAll();
        Card.OpenView();
        Assert.AreEqual('', Card.Lines.HeaderNo.Value, 'HeaderNo');
        Assert.AreEqual('', Card.Lines.LineNo.Value, 'LineNo');
        Assert.AreEqual('', Card.Lines.QTxt.Value, 'QTxt');
        Assert.AreEqual('', Card.Lines.QCd.Value, 'QCd');
        Assert.AreEqual('', Card.Lines.QInt.Value, 'QInt');
        Assert.AreEqual('', Card.Lines.QDec.Value, 'QDec');
        Assert.AreEqual('', Card.Lines.QBool.Value, 'QBool');
        Assert.AreEqual('', Card.Lines.QOpt.Value, 'QOpt');
        Assert.AreEqual('', Card.Lines.QDt.Value, 'QDt');
        Assert.AreEqual('', Card.Lines.QTm.Value, 'QTm');
        Assert.AreEqual('', Card.Lines.QDtTm.Value, 'QDtTm');
        Assert.AreEqual('', Card.Lines.QEn.Value, 'QEn');
        Assert.AreEqual('', Card.Lines.QBig.Value, 'QBig');
        Assert.AreEqual('', Card.Lines.QGd.Value, 'QGd');
        Assert.AreEqual('', Card.Lines.QDur.Value, 'QDur');
        Assert.AreEqual('', Card.Lines.QIntInit.Value, 'QIntInit');
        Assert.AreEqual('', Card.Lines.QDecInit.Value, 'QDecInit');
        Assert.AreEqual('', Card.Lines.QBoolInit.Value, 'QBoolInit');
    end;

    [Test]
    procedure NoRow_ListOverAnEmptyTable_EveryControlReadsBlank()
    var
        Card: TestPage "BNR Lines List";
    begin
        Initialize();
        Card.OpenView();
        Assert.AreEqual('', Card.HeaderNo.Value, 'HeaderNo');
        Assert.AreEqual('', Card.LineNo.Value, 'LineNo');
        Assert.AreEqual('', Card.QTxt.Value, 'QTxt');
        Assert.AreEqual('', Card.QCd.Value, 'QCd');
        Assert.AreEqual('', Card.QInt.Value, 'QInt');
        Assert.AreEqual('', Card.QDec.Value, 'QDec');
        Assert.AreEqual('', Card.QBool.Value, 'QBool');
        Assert.AreEqual('', Card.QOpt.Value, 'QOpt');
        Assert.AreEqual('', Card.QDt.Value, 'QDt');
        Assert.AreEqual('', Card.QTm.Value, 'QTm');
        Assert.AreEqual('', Card.QDtTm.Value, 'QDtTm');
        Assert.AreEqual('', Card.QEn.Value, 'QEn');
        Assert.AreEqual('', Card.QBig.Value, 'QBig');
        Assert.AreEqual('', Card.QGd.Value, 'QGd');
        Assert.AreEqual('', Card.QDur.Value, 'QDur');
        Assert.AreEqual('', Card.QIntInit.Value, 'QIntInit');
        Assert.AreEqual('', Card.QDecInit.Value, 'QDecInit');
        Assert.AreEqual('', Card.QBoolInit.Value, 'QBoolInit');
    end;

    [Test]
    procedure ListOverAnEmptyTable_TypedReadsAreTheTypeDefault()
    var
        Card: TestPage "BNR Lines List";
    begin
        Initialize();
        Card.OpenView();
        Assert.AreEqual(0, Card.QInt.AsInteger(), 'AsInteger');
        Assert.AreEqual(0, Card.QDec.AsDecimal(), 'AsDecimal');
        Assert.IsFalse(Card.QBool.AsBoolean(), 'AsBoolean');
        Assert.AreEqual(0D, Card.QDt.AsDate(), 'AsDate');
        Assert.AreEqual(0T, Card.QTm.AsTime(), 'AsTime');
        Assert.AreEqual(0, Card.QIntInit.AsInteger(), 'AsInteger of an InitValue field');
        Assert.AreEqual(0, Card.QDecInit.AsDecimal(), 'AsDecimal of an InitValue field');
        Assert.IsFalse(Card.QBoolInit.AsBoolean(), 'AsBoolean of an InitValue field');
    end;

    [Test]
    procedure NoRow_Contrast_ListWithARow_ReadsItsValues()
    var
        Card: TestPage "BNR Lines List";
    begin
        Initialize();
        InsertFullLine('H1', 10);
        Card.OpenView();
        Assert.AreEqual('42', Card.QInt.Value, 'a row is shown');
        Assert.AreEqual('text', Card.QTxt.Value, 'a row is shown (text)');
    end;

    [Test]
    procedure NoRow_CardOverAnEmptyTable_EveryControlReadsBlank()
    var
        Card: TestPage "BNR Line Card";
    begin
        Initialize();
        Card.OpenView();
        Assert.AreEqual('', Card.HeaderNo.Value, 'HeaderNo');
        Assert.AreEqual('', Card.LineNo.Value, 'LineNo');
        Assert.AreEqual('', Card.QTxt.Value, 'QTxt');
        Assert.AreEqual('', Card.QCd.Value, 'QCd');
        Assert.AreEqual('', Card.QInt.Value, 'QInt');
        Assert.AreEqual('', Card.QDec.Value, 'QDec');
        Assert.AreEqual('', Card.QBool.Value, 'QBool');
        Assert.AreEqual('', Card.QOpt.Value, 'QOpt');
        Assert.AreEqual('', Card.QDt.Value, 'QDt');
        Assert.AreEqual('', Card.QTm.Value, 'QTm');
        Assert.AreEqual('', Card.QDtTm.Value, 'QDtTm');
        Assert.AreEqual('', Card.QEn.Value, 'QEn');
        Assert.AreEqual('', Card.QBig.Value, 'QBig');
        Assert.AreEqual('', Card.QGd.Value, 'QGd');
        Assert.AreEqual('', Card.QDur.Value, 'QDur');
        Assert.AreEqual('', Card.QIntInit.Value, 'QIntInit');
        Assert.AreEqual('', Card.QDecInit.Value, 'QDecInit');
        Assert.AreEqual('', Card.QBoolInit.Value, 'QBoolInit');
    end;

    [Test]
    procedure NoRow_ListOverAnEmptyTable_AfterFirstAndLast_StillReadsBlank()
    var
        Card: TestPage "BNR Lines List";
    begin
        Initialize();
        Card.OpenView();
        Assert.IsFalse(Card.First(), 'First() has no row to go to');
        Assert.AreEqual('', Card.QInt.Value, 'after First()');
        Assert.IsFalse(Card.Last(), 'Last() has no row to go to');
        Assert.AreEqual('', Card.QInt.Value, 'after Last()');
    end;

    [Test]
    procedure NoRow_ListFilteredToNothing_EveryControlReadsBlank()
    var
        Card: TestPage "BNR Lines List";
    begin
        Initialize();
        InsertFullLine('H1', 10);
        Card.OpenView();
        Card.Filter.SetFilter("Header No.", 'ZZZ');
        Assert.AreEqual('', Card.QInt.Value, 'QInt');
        Assert.AreEqual('', Card.QDec.Value, 'QDec');
        Assert.AreEqual('', Card.QIntInit.Value, 'QIntInit');
    end;

    [Test]
    procedure NoRow_Contrast_EditableListOverAnEmptyTable_ShowsTheDraftLine_ReadingItsDefaults()
    var
        Card: TestPage "BNR Lines List";
    begin
        Initialize();
        Card.OpenEdit();
        Assert.AreEqual('0', Card.QInt.Value, 'QInt reads its default on the draft line');
        Assert.AreEqual('5', Card.QIntInit.Value, 'QIntInit reads its InitValue');
        Assert.AreEqual('Yes', Card.QBoolInit.Value, 'QBoolInit reads its InitValue');
        Assert.AreEqual(0, Card.QInt.AsInteger(), 'AsInteger on the draft line');
    end;

    [Test]
    procedure NoRow_Contrast_PartUnderAnEditableHostWithNoLines_ShowsTheDraftLine_ReadingItsDefaults()
    var
        Card: TestPage "BNR Card";
    begin
        Initialize();
        InsertFullLine('H1', 10);
        Card.OpenEdit();
        Card.GoToKey('H0');
        Assert.AreEqual('H0', Card.Lines.HeaderNo.Value, 'the draft line carries the link value');
        Assert.AreEqual('5', Card.Lines.QIntInit.Value, 'QIntInit reads its InitValue');
    end;

    [Test]
    procedure NoRow_Contrast_NewRowOnAnEmptyEditableList_ReadsTheValuesWrittenToIt()
    var
        Card: TestPage "BNR Lines List";
    begin
        Initialize();
        Card.OpenEdit();
        Card.New();
        Card.HeaderNo.SetValue('X');
        Card.LineNo.SetValue(1);
        Card.QInt.SetValue(5);
        Assert.AreEqual('5', Card.QInt.Value, 'the new row reads what was written');
        Assert.AreEqual(5, Card.QInt.AsInteger(), 'typed read of the new row');
        Assert.AreEqual('X', Card.HeaderNo.Value, 'the new row reads its key');
    end;

    local procedure AssertLineCardBlankFx(var Card: TestPage "BNR Line Card")
    begin
        Assert.AreEqual('', Card.HeaderNo.Value, 'HeaderNo');
        Assert.AreEqual('', Card.LineNo.Value, 'LineNo');
        Assert.AreEqual('', Card.QInt.Value, 'QInt');
        Assert.AreEqual('', Card.QIntInit.Value, 'QIntInit');
        Assert.AreEqual('', Card.QBoolInit.Value, 'QBoolInit');
        Assert.AreEqual(0, Card.QInt.AsInteger(), 'typed');
    end;

    // The actions below fill and position Rec from AL, the shape of Navigate: a list over a temporary
    // source table that shows nothing when it opens, then an action inserts rows and positions Rec.
    // CLAIM: once page code has put a row into the rowset and positioned Rec on it, the page shows
    // that row, whether or not the page showed nothing before.
    [Test]
    procedure NoRow_Action_TempListInsertAndFind_ShowsTheRow()
    var
        Card: TestPage "BNR Temp List";
    begin
        Initialize();
        Card.OpenView();
        Assert.AreEqual('', Card.QInt.Value, 'no row before the action');
        Card.InsertFind.Invoke();
        Assert.AreEqual('T', Card.HeaderNo.Value, 'the row the action inserted is shown');
        Assert.AreEqual('1', Card.LineNo.Value, 'its line');
        Assert.AreEqual('found', Card.QTxt.Value, 'its text');
        Assert.AreEqual('7', Card.QInt.Value, 'its integer');
        Assert.AreEqual(7, Card.QInt.AsInteger(), 'and its typed read');
    end;

    // CLAIM: the same when the action also calls CurrPage.Update.
    [Test]
    procedure NoRow_Action_TempListInsertFindUpdate_ShowsTheRow()
    var
        Card: TestPage "BNR Temp List";
    begin
        Initialize();
        Card.OpenView();
        Card.InsertFindUpdate.Invoke();
        Assert.AreEqual('7', Card.QInt.Value, 'the row is shown after CurrPage.Update');
        Assert.AreEqual('found', Card.QTxt.Value, 'its text');
    end;

    // CLAIM: an Insert alone leaves Rec on the inserted row, and the page shows it.
    [Test]
    procedure NoRow_Action_TempListInsertOnly_ShowsTheRow()
    var
        Card: TestPage "BNR Temp List";
    begin
        Initialize();
        Card.OpenView();
        Card.InsertOnly.Invoke();
        Assert.AreEqual('7', Card.QInt.Value, 'the inserted row is shown');
    end;

    // CLAIM: two inserts and a FindLast: the page shows the row Rec stands on, and First() shows the other.
    [Test]
    procedure NoRow_Action_TempListTwoInsertsAndFindLast_ShowsTheLastRow()
    var
        Card: TestPage "BNR Temp List";
    begin
        Initialize();
        Card.OpenView();
        Card.InsertTwoFindLast.Invoke();
        Assert.AreEqual('2', Card.QInt.Value, 'Rec stands on the second row');
        Card.First();
        Assert.AreEqual('1', Card.QInt.Value, 'First() shows the first row');
    end;

    // CLAIM: the same on a list over the stored table.
    [Test]
    procedure NoRow_Action_StoredListInsertAndFind_ShowsTheRow()
    var
        Card: TestPage "BNR Real List";
        Line: Record "BNR Line";
    begin
        Initialize();
        Line.DeleteAll();
        Card.OpenView();
        Assert.AreEqual('', Card.QInt.Value, 'no row before the action');
        Card.InsertFind.Invoke();
        Assert.AreEqual('4', Card.QInt.Value, 'the inserted row is shown');
        Assert.AreEqual('R', Card.HeaderNo.Value, 'its key');
    end;

    // CLAIM: an insert of a row the page's filter lets through, positioned on, is shown.
    [Test]
    procedure NoRow_Action_InsertOfARowTheFilterAdmits_ShowsTheRow()
    var
        Card: TestPage "BNR Get List";
    begin
        Initialize();
        Card.OpenView();
        Assert.AreEqual('', Card.QInt.Value, 'no row before the action');
        Card.InsertMatching.Invoke();
        Assert.AreEqual('3', Card.QInt.Value, 'the inserted row is shown');
        Assert.AreEqual('ZZZ', Card.HeaderNo.Value, 'its key');
    end;

    // CONTRAST: page code that changes only the buffer, with no row in the rowset, shows nothing.
    [Test]
    procedure NoRow_Action_TempListFieldsOnly_StaysBlank()
    var
        Card: TestPage "BNR Temp List";
    begin
        Initialize();
        Card.OpenView();
        Card.FieldsOnly.Invoke();
        Assert.AreEqual('', Card.QInt.Value, 'QInt');
        Assert.AreEqual('', Card.QTxt.Value, 'QTxt');
        Assert.AreEqual(0, Card.QInt.AsInteger(), 'typed');
    end;

    // CONTRAST: and so does a key set on Rec that was never inserted.
    [Test]
    procedure NoRow_Action_TempListKeyOnlyNoInsert_StaysBlank()
    var
        Card: TestPage "BNR Temp List";
    begin
        Initialize();
        Card.OpenView();
        Card.KeyOnly.Invoke();
        Assert.AreEqual('', Card.HeaderNo.Value, 'the key');
        Assert.AreEqual('', Card.QInt.Value, 'QInt');
    end;

    // CONTRAST: Get of a stored row the page's filter hides leaves the page showing nothing.
    [Test]
    procedure NoRow_Action_GetOfARowTheFilterHides_StaysBlank()
    var
        Card: TestPage "BNR Get List";
    begin
        Initialize();
        InsertFullLine('H1', 10);
        Card.OpenView();
        Card.GetRow.Invoke();
        Assert.AreEqual('', Card.QInt.Value, 'Rec is on a stored row the page filters out');
        Assert.AreEqual('', Card.QTxt.Value, 'its text');
    end;

    // CONTRAST: a row that test code inserts after the page opened is not shown before the list moves.
    [Test]
    procedure NoRow_TestCodeInsertAfterOpen_StaysBlankUntilTheListMoves()
    var
        Card: TestPage "BNR Real List";
        Line: Record "BNR Line";
    begin
        Initialize();
        Line.DeleteAll();
        Card.OpenView();
        Line.Init();
        Line."Header No." := 'R';
        Line."Line No." := 9;
        Line.QTxt := 'late';
        Line.QInt := 6;
        Line.Insert();
        Assert.AreEqual('', Card.QInt.Value, 'the page has not seen the new row');
        Assert.AreEqual('', Card.QTxt.Value, 'its text');
    end;

    // CLAIM: rows a temporary-source page inserts in its own OnOpenPage are shown, not blank.
    [Test]
    procedure NoRow_TempListInsertingInOnOpenPage_ShowsARow()
    var
        Card: TestPage "BNR Temp Open List";
    begin
        Initialize();
        Card.OpenView();
        Assert.AreEqual('T', Card.HeaderNo.Value, 'a row the page inserted is shown');
        Assert.AreNotEqual('', Card.QInt.Value, 'its integer is not blank');
    end;

    // CLAIM: a card over an empty table opened for editing reads blank (a card has no draft line) ...
    [Test]
    procedure NoRow_Card_OpenEditOverAnEmptyTable_ReadsBlank()
    var
        Card: TestPage "BNR Line Card";
        Line: Record "BNR Line";
    begin
        Initialize();
        Line.DeleteAll();
        Card.OpenEdit();
        AssertLineCardBlankFx(Card);
    end;

    // ... and one opened with OpenNew reads the defaults of the new record.
    [Test]
    procedure NoRow_Contrast_CardOpenNewOverAnEmptyTable_ReadsItsDefaults()
    var
        Card: TestPage "BNR Line Card";
        Line: Record "BNR Line";
    begin
        Initialize();
        Line.DeleteAll();
        Card.OpenNew();
        Assert.AreEqual('', Card.HeaderNo.Value, 'the key is blank');
        Assert.AreEqual('0', Card.LineNo.Value, 'the integer key reads its default');
        Assert.AreEqual('0', Card.QInt.Value, 'QInt reads its default');
        Assert.AreEqual('5', Card.QIntInit.Value, 'QIntInit reads its InitValue');
        Assert.AreEqual(0, Card.QInt.AsInteger(), 'typed');
    end;
}
