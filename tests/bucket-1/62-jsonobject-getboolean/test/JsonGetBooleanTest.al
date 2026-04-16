codeunit 81000 "Json GetBoolean Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: GetBoolean returns the correct boolean value for a key.
    // ------------------------------------------------------------------

    [Test]
    procedure GetBooleanReturnsTrueForTrueValue()
    var
        JObj: JsonObject;
        Val: Boolean;
    begin
        // [GIVEN] A JsonObject with a boolean true value
        JObj.Add('active', true);
        // [WHEN]  We call GetBoolean for that key
        Val := JObj.GetBoolean('active');
        // [THEN]  The result is true
        Assert.IsTrue(Val, 'GetBoolean should return true for a true value');
    end;

    [Test]
    procedure GetBooleanReturnsFalseForFalseValue()
    var
        JObj: JsonObject;
        Val: Boolean;
    begin
        // [GIVEN] A JsonObject with a boolean false value
        JObj.Add('enabled', false);
        // [WHEN]  We call GetBoolean for that key
        Val := JObj.GetBoolean('enabled');
        // [THEN]  The result is false
        Assert.IsFalse(Val, 'GetBoolean should return false for a false value');
    end;

    // ------------------------------------------------------------------
    // Negative: GetBoolean throws when the key is missing.
    // ------------------------------------------------------------------

    [Test]
    procedure GetBooleanThrowsForMissingKey()
    var
        JObj: JsonObject;
        Val: Boolean;
    begin
        // [GIVEN] An empty JsonObject
        // [WHEN]  We call GetBoolean for a key that does not exist
        // [THEN]  An error is raised
        asserterror Val := JObj.GetBoolean('missing');
        Assert.ExpectedError('missing');
    end;

    // ------------------------------------------------------------------
    // Negative: GetBoolean throws when the value is not a boolean.
    // ------------------------------------------------------------------

    [Test]
    procedure GetBooleanThrowsForNonBooleanValue()
    var
        JObj: JsonObject;
        Val: Boolean;
    begin
        // [GIVEN] A JsonObject with a text value at the key
        JObj.Add('score', 42);
        // [WHEN]  We call GetBoolean for a non-boolean key
        // [THEN]  An error is raised indicating a type mismatch
        asserterror Val := JObj.GetBoolean('score');
        Assert.ExpectedError('score');
    end;
}
