/// <summary>
/// Proves a BC progress (status) Dialog is a faithful headless no-op.
///
/// In a headless test run there is no client UI and no client callback, so the
/// real NavDialog.ALOpenAsync body NREs: every path dereferences
/// base.Tree.Session, whose tree-root session is not wired for an AL Dialog
/// variable opened during execution. Real BC already no-ops the WHOLE method for
/// non-UI (web-service) sessions, so a progress window has no observable effect on
/// business logic — the AL after Dialog.Open must run identically whether or not a
/// window appeared.
///
/// Regression target: the RS Document-Approvals path
///   Codeunit131101.EnableWorkflow → Record1501.Enabled_a45_OnValidate → ALOpenAsync
/// which previously threw NullReferenceException at NavDialog.ALOpenAsync.
/// </summary>
codeunit 60501 "Dialog NoOp Tests"
{
    Subtype = Test;

    var
        Window: Dialog;

    [Test]
    procedure ProgressDialog_OpenUpdateClose_IsNoOp_CodeAfterRuns()
    var
        Assert: Codeunit "Dialog NoOp Assert";
        Counter: Integer;
        Total: Integer;
    begin
        // [GIVEN] a progress dialog with an integer parameter token (#1#####).
        //         The token forces NavDialog.ALOpenAsync<T> to build a
        //         NavFormSourceExpressionGetterAsync[] — the exact path that NREd.
        Window.Open('Processing record #1######');

        // [WHEN] the dialog is updated and closed, and real work runs in between
        Total := 0;
        for Counter := 1 to 5 do begin
            Window.Update(1, Counter);
            Total += Counter;
        end;
        Window.Close();

        // [THEN] the code after Dialog.Open/Update/Close ran to completion and
        //        produced a concrete value (1+2+3+4+5 = 15). If the dialog had
        //        thrown (the old NRE) or swallowed control flow, Total would be 0.
        Assert.AreEqual(15, Total, 'Code after a progress Dialog must run and accumulate.');
    end;

    [Test]
    procedure ProgressDialog_DoesNotSwallowSubsequentError()
    var
        Assert: Codeunit "Dialog NoOp Assert";
    begin
        // [GIVEN] a progress dialog is opened (headless no-op) ...
        Window.Open('Working #1######');
        Window.Update(1, 1);

        // [WHEN] AL code after the dialog raises an error
        // [THEN] that error must still propagate — the dialog no-op must not
        //        disturb normal control flow or swallow the exception.
        asserterror Error('BOOM-AFTER-DIALOG');
        Assert.ExpectedError('BOOM-AFTER-DIALOG', GetLastErrorText());

        Window.Close();
    end;
}
