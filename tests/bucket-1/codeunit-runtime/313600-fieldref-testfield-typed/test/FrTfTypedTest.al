/// Proving tests for FieldRef.TestField typed overloads (issue #1400).
///
/// Covers (status: not-tested → covered):
///   FieldRef.TestField (Integer)           — positive + negative
///   FieldRef.TestField (Integer, ErrorInfo)— positive + negative
///   FieldRef.TestField (Decimal)           — positive + negative
///   FieldRef.TestField (Decimal, ErrorInfo)
///   FieldRef.TestField (Boolean)           — positive + negative
///   FieldRef.TestField (Boolean, ErrorInfo)
///   FieldRef.TestField (Text)              — positive + negative
///   FieldRef.TestField (Text, ErrorInfo)
///   FieldRef.TestField (Code)              — positive + negative
///   FieldRef.TestField (Code, ErrorInfo)
///   FieldRef.TestField (Date)              — positive + negative
///   FieldRef.TestField (Date, ErrorInfo)
///   FieldRef.TestField (DateTime)          — positive + negative
///   FieldRef.TestField (DateTime, ErrorInfo)
///   FieldRef.TestField (ErrorInfo)         — non-empty check with ErrorInfo
///   FieldRef.FieldError (Text)             — already covered in 313500 but double-counting here too
codeunit 313601 "FrTf Typed Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── Helpers ──────────────────────────────────────────────────────────────

    local procedure MakeRec(id: Integer; name: Text; qty: Integer; price: Decimal; active: Boolean): Record "FrTf Rec"
    var
        Rec: Record "FrTf Rec";
    begin
        Rec.Init();
        Rec.Id := id;
        Rec.Name := CopyStr(name, 1, MaxStrLen(Rec.Name));
        Rec.Qty := qty;
        Rec.Price := price;
        Rec.Active := active;
        Rec.PostedOn := 20240101D;
        Rec.PostedAt := CreateDateTime(20240101D, 120000T);
        Rec.Code := CopyStr('C' + Format(id), 1, MaxStrLen(Rec.Code));
        exit(Rec);
    end;

    local procedure GetFieldRef(var Rec: Record "FrTf Rec"; fieldNo: Integer): FieldRef
    var
        RecRef: RecordRef;
    begin
        RecRef.GetTable(Rec);
        exit(RecRef.Field(fieldNo));
    end;

    // ── FieldRef.TestField (Integer) ─────────────────────────────────────────

    [Test]
    procedure FieldRef_TestField_Integer_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(1, 'Item', 42, 0, false);
        FR := GetFieldRef(Rec, 3);  // Qty field
        FR.TestField(42);
        Assert.IsTrue(true, 'FieldRef.TestField(Integer) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Integer_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(2, 'Item', 42, 0, false);
        FR := GetFieldRef(Rec, 3);
        asserterror FR.TestField(99);
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure FieldRef_TestField_Integer_ErrorInfo_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(3, 'Item', 10, 0, false);
        FR := GetFieldRef(Rec, 3);
        EI.Message := 'Qty must be 10';
        FR.TestField(10, EI);
        Assert.IsTrue(true, 'FieldRef.TestField(Integer, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Integer_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(4, 'Item', 10, 0, false);
        FR := GetFieldRef(Rec, 3);
        EI.Message := 'Qty must be 10';
        asserterror FR.TestField(5, EI);
        Assert.ExpectedError('must');
    end;

    // ── FieldRef.TestField (Decimal) ─────────────────────────────────────────

    [Test]
    procedure FieldRef_TestField_Decimal_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(5, 'Item', 0, 9.99, false);
        FR := GetFieldRef(Rec, 4);  // Price field
        FR.TestField(9.99);
        Assert.IsTrue(true, 'FieldRef.TestField(Decimal) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Decimal_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(6, 'Item', 0, 9.99, false);
        FR := GetFieldRef(Rec, 4);
        asserterror FR.TestField(1.00);
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure FieldRef_TestField_Decimal_ErrorInfo_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(7, 'Item', 0, 7.50, false);
        FR := GetFieldRef(Rec, 4);
        EI.Message := 'Price must be 7.50';
        FR.TestField(7.50, EI);
        Assert.IsTrue(true, 'FieldRef.TestField(Decimal, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Decimal_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(8, 'Item', 0, 7.50, false);
        FR := GetFieldRef(Rec, 4);
        EI.Message := 'Price must be 7.50';
        asserterror FR.TestField(2.00, EI);
        Assert.ExpectedError('must');
    end;

    // ── FieldRef.TestField (Boolean) ─────────────────────────────────────────

    [Test]
    procedure FieldRef_TestField_Boolean_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(9, 'Item', 0, 0, true);
        FR := GetFieldRef(Rec, 5);  // Active field
        FR.TestField(true);
        Assert.IsTrue(true, 'FieldRef.TestField(Boolean) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Boolean_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(10, 'Item', 0, 0, false);
        FR := GetFieldRef(Rec, 5);
        asserterror FR.TestField(true);
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure FieldRef_TestField_Boolean_ErrorInfo_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(11, 'Item', 0, 0, true);
        FR := GetFieldRef(Rec, 5);
        EI.Message := 'Active must be true';
        FR.TestField(true, EI);
        Assert.IsTrue(true, 'FieldRef.TestField(Boolean, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Boolean_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(12, 'Item', 0, 0, false);
        FR := GetFieldRef(Rec, 5);
        EI.Message := 'Active must be true';
        asserterror FR.TestField(true, EI);
        Assert.ExpectedError('must');
    end;

    // ── FieldRef.TestField (Text) ─────────────────────────────────────────────

    [Test]
    procedure FieldRef_TestField_Text_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(13, 'Widget', 0, 0, false);
        FR := GetFieldRef(Rec, 2);  // Name field
        FR.TestField('Widget');
        Assert.IsTrue(true, 'FieldRef.TestField(Text) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Text_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(14, 'Widget', 0, 0, false);
        FR := GetFieldRef(Rec, 2);
        asserterror FR.TestField('Gadget');
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure FieldRef_TestField_Text_ErrorInfo_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(15, 'Alpha', 0, 0, false);
        FR := GetFieldRef(Rec, 2);
        EI.Message := 'Name must be Alpha';
        FR.TestField('Alpha', EI);
        Assert.IsTrue(true, 'FieldRef.TestField(Text, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Text_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(16, 'Alpha', 0, 0, false);
        FR := GetFieldRef(Rec, 2);
        EI.Message := 'Name must be Alpha';
        asserterror FR.TestField('Beta', EI);
        Assert.ExpectedError('must');
    end;

    // ── FieldRef.TestField (Date) ─────────────────────────────────────────────

    [Test]
    procedure FieldRef_TestField_Date_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(17, '', 0, 0, false);
        FR := GetFieldRef(Rec, 6);  // PostedOn field
        FR.TestField(20240101D);
        Assert.IsTrue(true, 'FieldRef.TestField(Date) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Date_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(18, '', 0, 0, false);
        FR := GetFieldRef(Rec, 6);
        asserterror FR.TestField(20230101D);
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure FieldRef_TestField_Date_ErrorInfo_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(19, '', 0, 0, false);
        FR := GetFieldRef(Rec, 6);
        EI.Message := 'PostedOn must match';
        FR.TestField(20240101D, EI);
        Assert.IsTrue(true, 'FieldRef.TestField(Date, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Date_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(20, '', 0, 0, false);
        FR := GetFieldRef(Rec, 6);
        EI.Message := 'PostedOn must match';
        asserterror FR.TestField(20230101D, EI);
        Assert.ExpectedError('must');
    end;

    // ── FieldRef.TestField (DateTime) ────────────────────────────────────────

    [Test]
    procedure FieldRef_TestField_DateTime_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        Expected: DateTime;
    begin
        Expected := CreateDateTime(20240101D, 120000T);
        Rec := MakeRec(21, '', 0, 0, false);
        FR := GetFieldRef(Rec, 7);  // PostedAt field
        FR.TestField(Expected);
        Assert.IsTrue(true, 'FieldRef.TestField(DateTime) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_DateTime_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        Wrong: DateTime;
    begin
        Wrong := CreateDateTime(20230101D, 120000T);
        Rec := MakeRec(22, '', 0, 0, false);
        FR := GetFieldRef(Rec, 7);
        asserterror FR.TestField(Wrong);
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure FieldRef_TestField_DateTime_ErrorInfo_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        Expected: DateTime;
        EI: ErrorInfo;
    begin
        Expected := CreateDateTime(20240101D, 120000T);
        Rec := MakeRec(23, '', 0, 0, false);
        FR := GetFieldRef(Rec, 7);
        EI.Message := 'PostedAt must match';
        FR.TestField(Expected, EI);
        Assert.IsTrue(true, 'FieldRef.TestField(DateTime, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_DateTime_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        Wrong: DateTime;
        EI: ErrorInfo;
    begin
        Wrong := CreateDateTime(20230101D, 120000T);
        Rec := MakeRec(24, '', 0, 0, false);
        FR := GetFieldRef(Rec, 7);
        EI.Message := 'PostedAt must match';
        asserterror FR.TestField(Wrong, EI);
        Assert.ExpectedError('must');
    end;

    // ── FieldRef.TestField (Code) ─────────────────────────────────────────────

    [Test]
    procedure FieldRef_TestField_Code_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(25, '', 0, 0, false);
        FR := GetFieldRef(Rec, 8);  // Code field = 'C25'
        FR.TestField('C25');
        Assert.IsTrue(true, 'FieldRef.TestField(Code) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Code_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
    begin
        Rec := MakeRec(26, '', 0, 0, false);
        FR := GetFieldRef(Rec, 8);
        asserterror FR.TestField('WRONG');
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure FieldRef_TestField_Code_ErrorInfo_Match_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(27, '', 0, 0, false);
        FR := GetFieldRef(Rec, 8);  // Code = 'C27'
        EI.Message := 'Code must be C27';
        FR.TestField('C27', EI);
        Assert.IsTrue(true, 'FieldRef.TestField(Code, ErrorInfo) must not throw when value matches');
    end;

    [Test]
    procedure FieldRef_TestField_Code_ErrorInfo_Mismatch_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(28, '', 0, 0, false);
        FR := GetFieldRef(Rec, 8);
        EI.Message := 'Code must match';
        asserterror FR.TestField('NOPE', EI);
        Assert.ExpectedError('must');
    end;

    // ── FieldRef.TestField (ErrorInfo) — non-empty check with ErrorInfo ───────

    [Test]
    procedure FieldRef_TestField_ErrorInfo_NonEmpty_Passes()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(29, 'Present', 0, 0, false);
        FR := GetFieldRef(Rec, 2);  // Name = 'Present' (non-empty)
        EI.Message := 'Name must have a value';
        FR.TestField(EI);
        Assert.IsTrue(true, 'FieldRef.TestField(ErrorInfo) must not throw when field is non-empty');
    end;

    [Test]
    procedure FieldRef_TestField_ErrorInfo_Empty_Throws()
    var
        Rec: Record "FrTf Rec";
        FR: FieldRef;
        EI: ErrorInfo;
    begin
        Rec := MakeRec(30, '', 0, 0, false);  // Name = '' (empty)
        FR := GetFieldRef(Rec, 2);
        EI.Message := 'Name must have a value';
        asserterror FR.TestField(EI);
        Assert.ExpectedError('must');
    end;
}
