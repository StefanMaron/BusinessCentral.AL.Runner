/// <summary>
/// Regression proof that the runner surfaces an AL call stack (not a C# stack trace)
/// when GetLastErrorCallStack is called after an asserterror block.
///
/// The positive test verifies the stack string contains the object type label
/// ("CodeUnit"), the numeric object ID, and the " by " / " version " tokens that
/// come from the owning app's manifest.
///
/// The negative/edge-case test verifies that if no error was raised, the function
/// returns an empty string (no phantom frames).
/// </summary>
codeunit 60900 "AL Call Stack Tests"
{
    Subtype = Test;

    [Test]
    procedure CallStack_AfterAssertError_ContainsALFrames()
    var
        Stack: Text;
    begin
        // Arrange — call a procedure that raises an AL error through a helper
        asserterror RaiseViaHelper();

        // Act — retrieve the call stack
        Stack := GetLastErrorCallStack();

        // Assert — the frame for THIS codeunit must appear with its object ID
        // (60900) and the mandatory app-tail tokens.
        Assert.IsTrue(
            Stack.Contains('(CodeUnit 60900)'),
            'Call stack must contain "(CodeUnit 60900)". Actual stack: ' + Stack);

        Assert.IsTrue(
            Stack.Contains(' by '),
            'Call stack must contain the " by " publisher token');

        Assert.IsTrue(
            Stack.Contains(' version '),
            'Call stack must contain the " version " token from app.json');

        // The helper frame must also appear with its object ID (60901)
        Assert.IsTrue(
            Stack.Contains('(CodeUnit 60901)'),
            'Call stack must contain "(CodeUnit 60901)" for the helper codeunit');

        // Every frame must include a line number
        Assert.IsTrue(
            Stack.Contains(' line '),
            'Call stack must contain " line " (AL source line numbers)');
    end;

    [Test]
    procedure CallStack_WhenNoError_ReturnsEmpty()
    var
        Stack: Text;
    begin
        // No error was raised — GetLastErrorCallStack must return empty / blank.
        Stack := GetLastErrorCallStack();
        Assert.AreEqual('', Stack, 'GetLastErrorCallStack must be empty when no error occurred');
    end;

    local procedure RaiseViaHelper()
    var
        Helper: Codeunit "AL Call Stack Helper";
    begin
        Helper.RaiseError();
    end;

    var
        Assert: Codeunit "AL Runner Assert";
}
