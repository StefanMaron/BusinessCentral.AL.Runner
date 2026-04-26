/// GetLastErrorObject tests (issue #853).
/// Proves GetLastErrorObject() returns a Variant after an error and does not
/// crash when there is no active error.
codeunit 117000 "GLEO Test"
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    // ── Basic non-crash ────────────────────────────────────────────────────────

    [Test]
    procedure GetLastErrorObject_AfterError_DoesNotCrash()
    var
        ErrObj: Variant;
    begin
        // Positive: GetLastErrorObject must not throw after asserterror.
        ClearLastError();
        asserterror Error('gleo-sentinel');
        ErrObj := GetLastErrorObject();
        // If we reach here, the call did not crash.
        Assert.IsTrue(true, 'GetLastErrorObject must complete without error after asserterror');
    end;

    [Test]
    procedure GetLastErrorObject_NoError_DoesNotCrash()
    var
        ErrObj: Variant;
    begin
        // Positive: GetLastErrorObject must not crash when called with no error.
        ClearLastError();
        ErrObj := GetLastErrorObject();
        Assert.IsTrue(true, 'GetLastErrorObject must not crash when no prior error');
    end;

    // ── Correlation with GetLastErrorText ─────────────────────────────────────

    [Test]
    procedure GetLastErrorObject_CoexistsWithGetLastErrorText()
    var
        ErrObj: Variant;
        ErrText: Text;
    begin
        // Positive: After asserterror, both GetLastErrorObject() and
        // GetLastErrorText() must be available without interfering.
        ClearLastError();
        asserterror Error('coexist-test');
        ErrObj := GetLastErrorObject();
        ErrText := GetLastErrorText();
        // GetLastErrorText should still return the error message
        Assert.AreEqual('coexist-test', ErrText,
            'GetLastErrorText must still return the error message after GetLastErrorObject call');
    end;

    [Test]
    procedure ClearLastError_ResetsErrorState()
    var
        ErrText: Text;
    begin
        // Positive: After ClearLastError(), GetLastErrorText returns ''.
        asserterror Error('pre-clear-error');
        ClearLastError();
        ErrText := GetLastErrorText();
        Assert.AreEqual('', ErrText,
            'GetLastErrorText must return empty string after ClearLastError');
    end;

    // ── Return type is a Variant ───────────────────────────────────────────────

    [Test]
    procedure GetLastErrorObject_ReturnsVariant_CanBeAssigned()
    var
        ErrObj: Variant;
        ErrObj2: Variant;
    begin
        // Positive: GetLastErrorObject result can be re-assigned to another Variant.
        ClearLastError();
        asserterror Error('variant-assign-test');
        ErrObj := GetLastErrorObject();
        ErrObj2 := ErrObj;
        Assert.IsTrue(true, 'GetLastErrorObject return value can be assigned to a Variant variable');
    end;
}
