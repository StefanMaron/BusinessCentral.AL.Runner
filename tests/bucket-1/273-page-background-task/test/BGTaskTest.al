codeunit 113004 "BGT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "BGT Helper";

    // ── EnqueueBackgroundTask ─────────────────────────────────────────────────

    [Test]
    procedure BGT_Enqueue_CompilationProof()
    begin
        // Positive: if MockCurrPage.EnqueueBackgroundTask is missing, Roslyn
        // compilation fails with CS1061 and the entire bucket goes RED.
        Assert.IsTrue(H.AllMethodsCompile(),
            'EnqueueBackgroundTask must compile on MockCurrPage');
    end;

    // ── CancelBackgroundTask ──────────────────────────────────────────────────

    [Test]
    procedure BGT_Cancel_CompilationProof()
    begin
        // Positive: CancelBackgroundTask must compile on MockCurrPage.
        Assert.IsTrue(H.AllMethodsCompile(),
            'CancelBackgroundTask must compile on MockCurrPage');
    end;

    // ── GetBackgroundParameters ───────────────────────────────────────────────

    [Test]
    procedure BGT_GetParams_CompilationProof()
    begin
        // Positive: Page.GetBackgroundParameters() must compile as a static call.
        Assert.IsTrue(H.AllMethodsCompile(),
            'GetBackgroundParameters must compile as Page.* static method');
    end;

    // ── SetBackgroundTaskResult ───────────────────────────────────────────────

    [Test]
    procedure BGT_SetResult_CompilationProof()
    begin
        // Positive: Page.SetBackgroundTaskResult must compile as a static call.
        Assert.IsTrue(H.AllMethodsCompile(),
            'SetBackgroundTaskResult must compile as Page.* static method');
    end;

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    procedure BGT_AssertError_StillWorks()
    begin
        // Negative: asserterror must work correctly after adding these stubs.
        asserterror Error('bgt-sentinel');
        Assert.ExpectedError('bgt-sentinel');
    end;
}
