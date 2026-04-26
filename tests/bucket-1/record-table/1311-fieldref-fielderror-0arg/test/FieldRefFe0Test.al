/// Tests for FieldRef.FieldError() 0-arg overload — issue #1428.
codeunit 1311001 "FieldRef FE0 Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";
        Helper: Codeunit "FieldRef FE0 Helper";

    // ── FieldRef.FieldError() — 0 args ───────────────────────────────────────

    [Test]
    procedure FieldError_0Arg_RaisesError()
    begin
        // [GIVEN] A RecordRef/FieldRef for field 2 (Description)
        // [WHEN] FieldRef.FieldError() is called with NO arguments
        asserterror Helper.CallFieldErrorNoArgs('X');
        // [THEN] An error is raised (no crash / CS1501 compile error)
        // The error text must contain the field caption 'Description'
        Assert.ExpectedError('Description');
    end;

    [Test]
    procedure FieldError_0Arg_ContainsDefaultMessage()
    begin
        // [GIVEN] A RecordRef/FieldRef for field 2 (Description)
        // [WHEN] FieldRef.FieldError() is called with NO arguments
        asserterror Helper.CallFieldErrorNoArgs('X');
        // [THEN] The error contains the default "must have a value" fragment
        Assert.ExpectedError('must have a value');
    end;

    [Test]
    procedure FieldError_1Arg_CustomMessage_StillWorks()
    begin
        // [GIVEN] A RecordRef/FieldRef for field 2 (Description)
        // [WHEN] FieldRef.FieldError('custom error') is called
        asserterror Helper.CallFieldErrorOneArg('X', 'custom error text');
        // [THEN] The error contains the custom message — existing overload unbroken
        Assert.ExpectedError('custom error text');
    end;
}
