/// Tests for ALTestFieldSafe(NavText/bool) and ALFind(NavText, bool) overloads.
///
/// Issues #1108 and #1109 report CS1503 compilation errors
/// (NavText → DataError, bool → string) in standard BC codeunits such as
/// PermissionHelper.  The root cause was missing overloads for patterns where
/// the BC transpiler emits:
///   - ALTestFieldSafe(fieldNo, NavType, NavText, ...)   when TestField receives a Text variable
///   - ALTestFieldSafe(fieldNo, NavType, bool, ...)      when TestField receives a Boolean variable
///   - ALFind(NavText, bool)                              for Find(SearchExpr, ForceNew) in some BC builds
///
/// All four ALTestFieldSafe variants and the two ALFind variants are exercised here.
codeunit 62102 "TFNvBl Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ========================================================================
    // TestField(Field, NavTextVar) — issues #1108 / #1109
    // BC emits: ALTestFieldSafe(fieldNo, NavType.Text, NavText)
    // ========================================================================

    [Test]
    procedure TestField_TextVar_Matches()
    var
        Rec:    Record "TFNvBl Record";
        Src:    Codeunit "TFNvBl Helper";
        ExpVal: Text;
    begin
        // [GIVEN] A record with Name = 'Widget'
        Rec.Init();
        Rec.Id := 1;
        Rec.Name := 'Widget';
        Rec.Insert(false);

        // [WHEN] TestField(Name, TextVariable) is called with the matching value
        ExpVal := 'Widget';
        Src.TestFieldTextVar(Rec, ExpVal);  // must not throw

        // [THEN] No exception raised — field matches
        Assert.IsTrue(true, 'TestField(Name, TextVar) must pass when value matches');
    end;

    [Test]
    procedure TestField_TextVar_Mismatch_Throws()
    var
        Rec:    Record "TFNvBl Record";
        Src:    Codeunit "TFNvBl Helper";
        ExpVal: Text;
    begin
        // [GIVEN] A record with Name = 'Widget'
        Rec.Init();
        Rec.Id := 2;
        Rec.Name := 'Widget';
        Rec.Insert(false);

        // [WHEN] TestField(Name, TextVariable) is called with a different value
        ExpVal := 'Gadget';

        // [THEN] An error is raised
        asserterror Src.TestFieldTextVar(Rec, ExpVal);
        Assert.ExpectedError('TestField failed');
    end;

    // ========================================================================
    // TestField(Field, BoolVar) — issues #1108 / #1109
    // BC emits: ALTestFieldSafe(fieldNo, NavType.Boolean, bool)
    // ========================================================================

    [Test]
    procedure TestField_BoolVar_Matches()
    var
        Rec:     Record "TFNvBl Record";
        Src:     Codeunit "TFNvBl Helper";
        ExpBool: Boolean;
    begin
        // [GIVEN] A record with Flag = true
        Rec.Init();
        Rec.Id := 3;
        Rec.Flag := true;
        Rec.Insert(false);

        // [WHEN] TestField(Flag, BoolVariable) is called with true
        ExpBool := true;
        Src.TestFieldBoolVar(Rec, ExpBool);  // must not throw

        // [THEN] No exception raised — field matches
        Assert.IsTrue(true, 'TestField(Flag, BoolVar) must pass when value matches');
    end;

    [Test]
    procedure TestField_BoolVar_Mismatch_Throws()
    var
        Rec:     Record "TFNvBl Record";
        Src:     Codeunit "TFNvBl Helper";
        ExpBool: Boolean;
    begin
        // [GIVEN] A record with Flag = false
        Rec.Init();
        Rec.Id := 4;
        Rec.Flag := false;
        Rec.Insert(false);

        // [WHEN] TestField(Flag, BoolVariable) is called with true
        ExpBool := true;

        // [THEN] An error is raised
        asserterror Src.TestFieldBoolVar(Rec, ExpBool);
        Assert.ExpectedError('TestField failed');
    end;

    // ========================================================================
    // TestField(Field, TextVar, ErrorInfo) — issues #1108 / #1109
    // BC emits: ALTestFieldSafe(fieldNo, NavType.Text, NavText, NavALErrorInfo)
    // ========================================================================

    [Test]
    procedure TestField_TextVarEI_Matches()
    var
        Rec:    Record "TFNvBl Record";
        Src:    Codeunit "TFNvBl Helper";
        ExpVal: Text;
        EI:     ErrorInfo;
    begin
        // [GIVEN] A record with Name = 'Alpha'
        Rec.Init();
        Rec.Id := 5;
        Rec.Name := 'Alpha';
        Rec.Insert(false);

        // [WHEN] TestField(Name, TextVar, ErrorInfo) with matching value
        ExpVal := 'Alpha';
        EI.Message := 'Name must be Alpha';
        Src.TestFieldTextVarEI(Rec, ExpVal, EI);  // must not throw

        // [THEN] No exception raised
        Assert.IsTrue(true, 'TestField(Name, TextVar, EI) must pass when value matches');
    end;

    [Test]
    procedure TestField_TextVarEI_Mismatch_Throws()
    var
        Rec:    Record "TFNvBl Record";
        Src:    Codeunit "TFNvBl Helper";
        ExpVal: Text;
        EI:     ErrorInfo;
    begin
        // [GIVEN] A record with Name = 'Alpha'
        Rec.Init();
        Rec.Id := 6;
        Rec.Name := 'Alpha';
        Rec.Insert(false);

        // [WHEN] TestField(Name, TextVar, ErrorInfo) with a different value
        ExpVal := 'Beta';
        EI.Message := 'Name must be Alpha';

        // [THEN] An error is raised
        asserterror Src.TestFieldTextVarEI(Rec, ExpVal, EI);
        Assert.ExpectedError('TestField failed');
    end;

    // ========================================================================
    // TestField(Field, BoolVar, ErrorInfo) — issues #1108 / #1109
    // BC emits: ALTestFieldSafe(fieldNo, NavType.Boolean, bool, NavALErrorInfo)
    // ========================================================================

    [Test]
    procedure TestField_BoolVarEI_Matches()
    var
        Rec:     Record "TFNvBl Record";
        Src:     Codeunit "TFNvBl Helper";
        ExpBool: Boolean;
        EI:      ErrorInfo;
    begin
        // [GIVEN] A record with Flag = true
        Rec.Init();
        Rec.Id := 7;
        Rec.Flag := true;
        Rec.Insert(false);

        // [WHEN] TestField(Flag, BoolVar, ErrorInfo) with true
        ExpBool := true;
        EI.Message := 'Flag must be true';
        Src.TestFieldBoolVarEI(Rec, ExpBool, EI);  // must not throw

        // [THEN] No exception raised
        Assert.IsTrue(true, 'TestField(Flag, BoolVar, EI) must pass when value matches');
    end;

    [Test]
    procedure TestField_BoolVarEI_Mismatch_Throws()
    var
        Rec:     Record "TFNvBl Record";
        Src:     Codeunit "TFNvBl Helper";
        ExpBool: Boolean;
        EI:      ErrorInfo;
    begin
        // [GIVEN] A record with Flag = false
        Rec.Init();
        Rec.Id := 8;
        Rec.Flag := false;
        Rec.Insert(false);

        // [WHEN] TestField(Flag, BoolVar, ErrorInfo) with true
        ExpBool := true;
        EI.Message := 'Flag must be true';

        // [THEN] An error is raised
        asserterror Src.TestFieldBoolVarEI(Rec, ExpBool, EI);
        Assert.ExpectedError('TestField failed');
    end;

    // ========================================================================
    // Find(SearchVar) — NavText search expression
    // BC emits: ALFind(DataError, NavText) — resolves via NavText implicit string cast
    // ========================================================================

    [Test]
    procedure Find_WithTextVar_ReturnsTrue()
    var
        Rec:  Record "TFNvBl Record";
        Src:  Codeunit "TFNvBl Helper";
        Expr: Text;
    begin
        // [GIVEN] One record in the table
        Rec.Init();
        Rec.Id := 9;
        Rec.Name := 'FindMe';
        Rec.Insert(false);

        // [WHEN] Find(TextVar) using '=' search expression stored in a variable
        Expr := '=';

        // [THEN] Record is found
        Assert.IsTrue(Src.FindWithTextVar(Rec, Expr), 'Find with Text variable must locate the record');
    end;

    [Test]
    procedure Find_WithTextVar_NoRecords_ReturnsFalse()
    var
        Rec:    Record "TFNvBl Record";
        Src:    Codeunit "TFNvBl Helper";
        Expr:   Text;
    begin
        // [GIVEN] Table is empty (previous tests use different IDs, no insert here)
        Rec.Init();
        Rec.SetRange(Id, 99999);  // filter to a range that has no records

        // [WHEN] Find on an empty filtered set
        Expr := '=';

        // [THEN] Find returns false
        Assert.IsFalse(Src.FindWithTextVar(Rec, Expr), 'Find on empty set must return false');
    end;
}
