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
    procedure PartShowingNoRow_EveryControlReadsBlank()
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
    procedure PartShowingNoRow_TypedReadsAreTheTypeDefault()
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
    procedure PartOfAHostOverAnEmptyTable_EveryControlReadsBlank()
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
    procedure ListOverAnEmptyTable_EveryControlReadsBlank()
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
    procedure ListWithARow_ReadsItsValues()
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
    procedure CardOverAnEmptyTable_EveryControlReadsBlank()
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
}
