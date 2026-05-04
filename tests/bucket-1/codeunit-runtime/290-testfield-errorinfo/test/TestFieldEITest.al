/// Tests for Record.TestField overloads that accept an ErrorInfo argument.
///
/// BC emits:
///   TestField(Field, ErrorInfo)       → ALTestFieldSafe(fieldNo, NavType, errorInfo)
///   TestField(Field, Value, ErrorInfo)→ ALTestFieldSafe(fieldNo, NavType, value, errorInfo)
///
/// Before fix: both produced CS1501 (no 4-arg overload) or CS1503 (wrong type match).
/// After fix:  compile and execute correctly — the ErrorInfo is used as error context
///             when the check fails, and ignored when it passes (issues #1083, #1084, #1089).
codeunit 166001 "TFEi Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // TestField(Field, Value, ErrorInfo) — 4-arg form (issue #1083)
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_BoolValue_ErrorInfo_Passes()
    var
        Rec: Record "TFEi Record";
        EI: ErrorInfo;
    begin
        // [GIVEN] A record with Flag = true
        Rec.Init();
        Rec.Id := 1;
        Rec.Flag := true;
        Rec.Insert(false);

        // [WHEN] TestField(Flag, true, ErrorInfo) — value matches
        EI.Message := 'Flag must be true';
        Rec.TestField(Flag, true, EI);

        // [THEN] No error raised
        Assert.IsTrue(true, 'TestField must not raise an error when Boolean value matches');
    end;

    [Test]
    procedure TestField_BoolValue_ErrorInfo_ThrowsOnMismatch()
    var
        Rec: Record "TFEi Record";
        EI: ErrorInfo;
    begin
        // [GIVEN] A record with Flag = false
        Rec.Init();
        Rec.Id := 2;
        Rec.Flag := false;
        Rec.Insert(false);

        // [WHEN] TestField(Flag, true, ErrorInfo) — value does not match
        EI.Message := 'Flag must be true';

        // [THEN] An error is raised
        asserterror Rec.TestField(Flag, true, EI);
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure TestField_TextValue_ErrorInfo_Passes()
    var
        Rec: Record "TFEi Record";
        EI: ErrorInfo;
    begin
        // [GIVEN] A record with Name = 'Alpha'
        Rec.Init();
        Rec.Id := 3;
        Rec.Name := 'Alpha';
        Rec.Insert(false);

        // [WHEN] TestField(Name, 'Alpha', ErrorInfo) — value matches (issue #1089 pattern)
        EI.Message := 'Name must be Alpha';
        Rec.TestField(Name, 'Alpha', EI);

        // [THEN] No error raised
        Assert.IsTrue(true, 'TestField must not raise an error when Text value matches');
    end;

    [Test]
    procedure TestField_TextValue_ErrorInfo_ThrowsOnMismatch()
    var
        Rec: Record "TFEi Record";
        EI: ErrorInfo;
    begin
        // [GIVEN] A record with Name = 'Alpha'
        Rec.Init();
        Rec.Id := 4;
        Rec.Name := 'Alpha';
        Rec.Insert(false);

        // [WHEN] TestField(Name, 'Beta', ErrorInfo) — value does not match
        EI.Message := 'Name must be Alpha';

        // [THEN] An error is raised
        asserterror Rec.TestField(Name, 'Beta', EI);
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure TestField_IntValue_ErrorInfo_Passes()
    var
        Rec: Record "TFEi Record";
        EI: ErrorInfo;
    begin
        // [GIVEN] A record with Qty = 42
        Rec.Init();
        Rec.Id := 5;
        Rec.Qty := 42;
        Rec.Insert(false);

        // [WHEN] TestField(Qty, 42, ErrorInfo) — value matches
        EI.Message := 'Qty must be 42';
        Rec.TestField(Qty, 42, EI);

        // [THEN] No error raised
        Assert.IsTrue(true, 'TestField must not raise an error when Integer value matches');
    end;

    [Test]
    procedure TestField_IntValue_ErrorInfo_ThrowsOnMismatch()
    var
        Rec: Record "TFEi Record";
        EI: ErrorInfo;
    begin
        // [GIVEN] A record with Qty = 42
        Rec.Init();
        Rec.Id := 6;
        Rec.Qty := 42;
        Rec.Insert(false);

        // [WHEN] TestField(Qty, 99, ErrorInfo) — value does not match
        EI.Message := 'Qty must be 42';

        // [THEN] An error is raised
        asserterror Rec.TestField(Qty, 99, EI);
        Assert.ExpectedError('must');
    end;

    // -----------------------------------------------------------------------
    // TestField(Field, ErrorInfo) — 3-arg form, ErrorInfo as expected-value slot
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_NonEmpty_ErrorInfo_Passes()
    var
        Rec: Record "TFEi Record";
        EI: ErrorInfo;
    begin
        // [GIVEN] A record with Name = 'Beta' (non-empty)
        Rec.Init();
        Rec.Id := 7;
        Rec.Name := 'Beta';
        Rec.Insert(false);

        // [WHEN] TestField(Name, ErrorInfo) — non-empty check passes
        EI.Message := 'Name must have a value';
        Rec.TestField(Name, EI);

        // [THEN] No error raised
        Assert.IsTrue(true, 'TestField must not raise error when field is non-empty');
    end;

    [Test]
    procedure TestField_NonEmpty_ErrorInfo_ThrowsWhenEmpty()
    var
        Rec: Record "TFEi Record";
        EI: ErrorInfo;
    begin
        // [GIVEN] A record with empty Name
        Rec.Init();
        Rec.Id := 8;
        // Name is empty (default)

        // [WHEN] TestField(Name, ErrorInfo) — field is empty
        EI.Message := 'Name must have a value';

        // [THEN] An error is raised because Name is empty
        asserterror Rec.TestField(Name, EI);
        Assert.ExpectedError('must');
    end;

    [Test]
    procedure TestField_Bool_NonEmpty_ErrorInfo_Passes()
    var
        Rec: Record "TFEi Record";
        EI: ErrorInfo;
    begin
        // [GIVEN] A record with Flag = true
        Rec.Init();
        Rec.Id := 9;
        Rec.Flag := true;
        Rec.Insert(false);

        // [WHEN] TestField(Flag, ErrorInfo) — Flag is true (non-default/non-empty)
        // The BC transpiler emits ALTestFieldSafe(fieldNo, NavType, errorInfo)
        // which triggered CS1503 (NavALErrorInfo treated as NavValue) — issue #1084
        EI.Message := 'Flag must have a value';
        Rec.TestField(Flag, EI);

        // [THEN] No error raised
        Assert.IsTrue(true, 'TestField must not raise error when boolean field is true');
    end;
}
