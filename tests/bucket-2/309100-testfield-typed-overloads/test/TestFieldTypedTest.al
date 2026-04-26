/// Tests for Table.TestField typed and ErrorInfo overloads — issue #1369.
///
/// Covers:
///   TestField(Field, Decimal)           — positive + negative
///   TestField(Field, Decimal, ErrorInfo)— positive + negative
///   TestField(Field, DateTime)          — positive + negative
///   TestField(Field, DateTime, ErrorInfo)
///   TestField(Field, Boolean)           — positive + negative
///   TestField(Field, Boolean, ErrorInfo)— positive + negative
///   TestField(Field, Integer)           — positive + negative
///   TestField(Field, Integer, ErrorInfo)
///   TestField(Field, Code)              — positive + negative
///   TestField(Field, Code, ErrorInfo)
///   TestField(Field, Text)              — positive + negative
///   TestField(Field, Text, ErrorInfo)
///   TestField(Field, ErrorInfo)         — non-empty check with ErrorInfo
codeunit 309101 "TFTO Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    local procedure MakeRec(No: Code[20]; Qty: Integer; Price: Decimal; Active: Boolean; PostedAt: DateTime; Name: Text[50]): Record "TFTO Item"
    var
        Rec: Record "TFTO Item";
    begin
        Rec.Init();
        Rec."No." := No;
        Rec.Qty := Qty;
        Rec.Price := Price;
        Rec.Active := Active;
        Rec."Posted At" := PostedAt;
        Rec.Name := Name;
        exit(Rec);
    end;

    // -----------------------------------------------------------------------
    // Decimal overloads
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_Decimal_Match_Passes()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('D1', 0, 9.99, false, 0DT, '');
        Rec.TestField(Price, 9.99);
        Assert.IsTrue(true, 'TestField(Decimal) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Decimal_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('D2', 0, 9.99, false, 0DT, '');
        asserterror Rec.TestField(Price, 1.00);
        Assert.ExpectedError('TestField failed');
    end;

    [Test]
    procedure TestField_Decimal_ErrorInfo_Match_Passes()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('D3', 0, 7.50, false, 0DT, '');
        EI.Message := 'Price must be 7.50';
        Rec.TestField(Price, 7.50, EI);
        Assert.IsTrue(true, 'TestField(Decimal, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Decimal_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('D4', 0, 7.50, false, 0DT, '');
        EI.Message := 'Price must be 7.50';
        asserterror Rec.TestField(Price, 2.00, EI);
        Assert.ExpectedError('TestField failed');
    end;

    // -----------------------------------------------------------------------
    // DateTime overloads
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_DateTime_Match_Passes()
    var
        Rec: Record "TFTO Item";
        Expected: DateTime;
    begin
        Expected := CreateDateTime(20240101D, 120000T);
        Rec := MakeRec('DT1', 0, 0, false, Expected, '');
        Rec.TestField("Posted At", Expected);
        Assert.IsTrue(true, 'TestField(DateTime) must not throw when value matches');
    end;

    [Test]
    procedure TestField_DateTime_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
        Stored: DateTime;
        Wrong: DateTime;
    begin
        Stored := CreateDateTime(20240101D, 120000T);
        Wrong := CreateDateTime(20230101D, 120000T);
        Rec := MakeRec('DT2', 0, 0, false, Stored, '');
        asserterror Rec.TestField("Posted At", Wrong);
        Assert.ExpectedError('TestField failed');
    end;

    [Test]
    procedure TestField_DateTime_ErrorInfo_Match_Passes()
    var
        Rec: Record "TFTO Item";
        Expected: DateTime;
        EI: ErrorInfo;
    begin
        Expected := CreateDateTime(20240101D, 120000T);
        Rec := MakeRec('DT3', 0, 0, false, Expected, '');
        EI.Message := 'PostedAt must match';
        Rec.TestField("Posted At", Expected, EI);
        Assert.IsTrue(true, 'TestField(DateTime, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure TestField_DateTime_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
        Stored: DateTime;
        Wrong: DateTime;
        EI: ErrorInfo;
    begin
        Stored := CreateDateTime(20240101D, 120000T);
        Wrong := CreateDateTime(20230101D, 120000T);
        Rec := MakeRec('DT4', 0, 0, false, Stored, '');
        EI.Message := 'PostedAt must match';
        asserterror Rec.TestField("Posted At", Wrong, EI);
        Assert.ExpectedError('TestField failed');
    end;

    // -----------------------------------------------------------------------
    // Boolean overloads
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_Boolean_Match_Passes()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('B1', 0, 0, true, 0DT, '');
        Rec.TestField(Active, true);
        Assert.IsTrue(true, 'TestField(Boolean) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Boolean_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('B2', 0, 0, false, 0DT, '');
        asserterror Rec.TestField(Active, true);
        Assert.ExpectedError('TestField failed');
    end;

    [Test]
    procedure TestField_Boolean_ErrorInfo_Match_Passes()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('B3', 0, 0, true, 0DT, '');
        EI.Message := 'Active must be true';
        Rec.TestField(Active, true, EI);
        Assert.IsTrue(true, 'TestField(Boolean, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Boolean_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('B4', 0, 0, false, 0DT, '');
        EI.Message := 'Active must be true';
        asserterror Rec.TestField(Active, true, EI);
        Assert.ExpectedError('TestField failed');
    end;

    // -----------------------------------------------------------------------
    // Integer overloads
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_Integer_Match_Passes()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('I1', 42, 0, false, 0DT, '');
        Rec.TestField(Qty, 42);
        Assert.IsTrue(true, 'TestField(Integer) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Integer_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('I2', 42, 0, false, 0DT, '');
        asserterror Rec.TestField(Qty, 99);
        Assert.ExpectedError('TestField failed');
    end;

    [Test]
    procedure TestField_Integer_ErrorInfo_Match_Passes()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('I3', 10, 0, false, 0DT, '');
        EI.Message := 'Qty must be 10';
        Rec.TestField(Qty, 10, EI);
        Assert.IsTrue(true, 'TestField(Integer, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Integer_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('I4', 10, 0, false, 0DT, '');
        EI.Message := 'Qty must be 10';
        asserterror Rec.TestField(Qty, 5, EI);
        Assert.ExpectedError('TestField failed');
    end;

    // -----------------------------------------------------------------------
    // Code overloads
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_Code_Match_Passes()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('C001', 0, 0, false, 0DT, '');
        Rec.TestField("No.", 'C001');
        Assert.IsTrue(true, 'TestField(Code) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Code_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('C002', 0, 0, false, 0DT, '');
        asserterror Rec.TestField("No.", 'C999');
        Assert.ExpectedError('TestField failed');
    end;

    [Test]
    procedure TestField_Code_ErrorInfo_Match_Passes()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('C003', 0, 0, false, 0DT, '');
        EI.Message := 'No. must be C003';
        Rec.TestField("No.", 'C003', EI);
        Assert.IsTrue(true, 'TestField(Code, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Code_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('C004', 0, 0, false, 0DT, '');
        EI.Message := 'No. must be C004';
        asserterror Rec.TestField("No.", 'C999', EI);
        Assert.ExpectedError('TestField failed');
    end;

    // -----------------------------------------------------------------------
    // Text overloads
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_Text_Match_Passes()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('T1', 0, 0, false, 0DT, 'Widget');
        Rec.TestField(Name, 'Widget');
        Assert.IsTrue(true, 'TestField(Text) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Text_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
    begin
        Rec := MakeRec('T2', 0, 0, false, 0DT, 'Widget');
        asserterror Rec.TestField(Name, 'Gadget');
        Assert.ExpectedError('TestField failed');
    end;

    [Test]
    procedure TestField_Text_ErrorInfo_Match_Passes()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('T3', 0, 0, false, 0DT, 'Alpha');
        EI.Message := 'Name must be Alpha';
        Rec.TestField(Name, 'Alpha', EI);
        Assert.IsTrue(true, 'TestField(Text, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure TestField_Text_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('T4', 0, 0, false, 0DT, 'Alpha');
        EI.Message := 'Name must be Alpha';
        asserterror Rec.TestField(Name, 'Beta', EI);
        Assert.ExpectedError('TestField failed');
    end;

    // -----------------------------------------------------------------------
    // TestField(Field, ErrorInfo) — non-empty check with ErrorInfo
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_ErrorInfo_NonEmpty_Passes()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('E1', 0, 0, false, 0DT, 'Present');
        EI.Message := 'Name must have a value';
        Rec.TestField(Name, EI);
        Assert.IsTrue(true, 'TestField(ErrorInfo) must not throw when field is non-empty');
    end;

    [Test]
    procedure TestField_ErrorInfo_Empty_Throws()
    var
        Rec: Record "TFTO Item";
        EI: ErrorInfo;
    begin
        Rec := MakeRec('E2', 0, 0, false, 0DT, '');
        EI.Message := 'Name must have a value';
        asserterror Rec.TestField(Name, EI);
        Assert.ExpectedError('TestField failed');
    end;
}
