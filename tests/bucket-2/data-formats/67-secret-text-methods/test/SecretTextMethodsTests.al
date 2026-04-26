codeunit 58201 "STM Secret Text Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "STM Secret Text Helper";

    // -----------------------------------------------------------------------
    // IsEmpty()
    // -----------------------------------------------------------------------

    [Test]
    procedure IsEmpty_UninitialisedSecret_ReturnsTrue()
    var
        secret: SecretText;
    begin
        // [GIVEN] A default-initialised SecretText (no assignment)
        // [WHEN] IsEmpty() called
        // [THEN] Returns true
        Assert.IsTrue(secret.IsEmpty(), 'Uninitialised SecretText must be empty');
    end;

    [Test]
    procedure IsEmpty_AssignedFromText_ReturnsFalse()
    begin
        // [GIVEN] A SecretText assigned from a non-empty literal
        // [WHEN] IsEmpty() called via helper
        // [THEN] Returns false
        Assert.IsFalse(Helper.IsAssignedSecretEmpty('my-secret'), 'SecretText assigned from non-empty text must not be empty');
    end;

    [Test]
    procedure IsEmpty_AssignedFromEmptyText_ReturnsTrue()
    begin
        // [GIVEN] A SecretText assigned from an empty string
        // [WHEN] IsEmpty() called via helper
        // [THEN] Returns true
        Assert.IsTrue(Helper.IsAssignedSecretEmpty(''), 'SecretText assigned from empty text must be empty');
    end;

    [Test]
    procedure IsEmpty_PassedAsParam_EmptySecret_ReturnsTrue()
    var
        secret: SecretText;
    begin
        // [GIVEN] An empty SecretText passed as a parameter
        Assert.IsTrue(Helper.IsSecretEmpty(secret), 'Empty SecretText parameter: IsEmpty must return true');
    end;

    // -----------------------------------------------------------------------
    // Unwrap()
    // -----------------------------------------------------------------------

    [Test]
    procedure Unwrap_ReturnsOriginalText()
    begin
        // [GIVEN] A SecretText assigned from 'api-key-123'
        // [WHEN] Unwrap() called
        // [THEN] Returns 'api-key-123'
        Assert.AreEqual('api-key-123', Helper.UnwrapSecret('api-key-123'), 'Unwrap must return the original text value');
    end;

    [Test]
    procedure Unwrap_EmptySecret_ReturnsEmpty()
    begin
        // [GIVEN] A SecretText assigned from ''
        // [WHEN] Unwrap() called
        // [THEN] Returns ''
        Assert.AreEqual('', Helper.UnwrapSecret(''), 'Unwrap of empty secret must return empty string');
    end;

    [Test]
    procedure Unwrap_PreservesCase()
    begin
        // [GIVEN] A mixed-case secret
        // [WHEN] Unwrap() called
        // [THEN] Original casing preserved
        Assert.AreEqual('ABC-xyz-123', Helper.UnwrapSecret('ABC-xyz-123'), 'Unwrap must preserve casing');
    end;

    // -----------------------------------------------------------------------
    // SecretStrSubstNo()
    // -----------------------------------------------------------------------

    [Test]
    procedure SecretStrSubstNo_SubstitutesArgument()
    begin
        // [GIVEN] Format 'Bearer %1' with token 'my-token'
        // [WHEN] SecretStrSubstNo called
        // [THEN] Result unwraps to 'Bearer my-token'
        Assert.AreEqual('Bearer my-token', Helper.BuildSecretMessage('Bearer %1', 'my-token'),
            'SecretStrSubstNo must substitute %1 with the argument');
    end;

    [Test]
    procedure SecretStrSubstNo_ResultIsNotEmpty()
    begin
        // [GIVEN] A non-empty format with a non-empty argument
        // [WHEN] SecretStrSubstNo called and result checked with IsEmpty
        // [THEN] Result is not empty
        Assert.IsFalse(Helper.IsAssignedSecretEmpty(Helper.BuildSecretMessage('key=%1', 'value')),
            'SecretStrSubstNo result assigned back must not be empty');
    end;
}
