// StartSession called from the runner's install pass (#2826 follow-up, #3292).
//
// Two claims, both read back from rows "ITS Installer".OnInstallAppPerCompany writes:
//
//   * #2805's guard is INERT outside a [Test]. BC refuses StartSession from inside a [Test]
//     unless the TestRunner declares TestIsolation = Disabled (corpus codeunit 60397); an install
//     trigger is not a test. If the guard fired here, the install trigger would throw before it
//     writes INSTALL-RESULT, so the row's existence is the proof.
//   * The runner's install-pass flag refuses StartSession the way BC's AppInstallationContext
//     check does: false, SessionId untouched, worker not run. The BC behaviour itself is pinned
//     upstream by corpus codeunit 60449; this test pins that the runner's install pass sets the
//     flag its StartSession reads.
//
// INSTALL-RESULT."Value" encodes the outcome so one field discriminates all three wrong answers:
//   1    -> StartSession returned true (the install flag was not set, or not consulted);
//   777  -> returned false and left SessionId alone (correct);
//   else -> returned false but wrote a session id first (refused after allocating one).
codeunit 60718 "ITS StartSession Outside Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "ITS Assert";

    [Test]
    procedure InstallTrigger_StartSession_IsNotRefusedByTheTestGuard()
    var
        Marker: Record "ITS Session Marker";
    begin
        Assert.IsTrue(Marker.Get('INSTALL-RESULT'),
            'the install trigger must have run past its StartSession call and written INSTALL-RESULT. ' +
            'An absent row means the [Test]-only guard fired outside a test.');
    end;

    [Test]
    procedure InstallTrigger_StartSession_ReturnsFalse_LeavesSessionId_RunsNoWorker()
    var
        Marker: Record "ITS Session Marker";
    begin
        Assert.IsTrue(Marker.Get('INSTALL-RESULT'), 'precondition: the install trigger wrote INSTALL-RESULT');
        Assert.AreEqual(777, Marker."Value",
            'StartSession during the install pass must return false without writing SessionId ' +
            '(1 = it returned true; any other value = it wrote a session id)');
        Assert.IsFalse(Marker.Get('FROM-INSTALL'),
            'the worker passed to StartSession during the install pass must not have run');
    end;
}
