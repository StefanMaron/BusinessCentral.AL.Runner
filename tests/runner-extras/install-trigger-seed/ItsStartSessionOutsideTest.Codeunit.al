// Issue #2826 follow-up: #2805's StartSession guard must be INERT outside a [Test].
//
// BC refuses StartSession from inside a [Test] unless the TestRunner declares
// TestIsolation = Disabled — real BC, pinned upstream by corpus codeunit 60397 on all eight
// versions, and implemented by the runner. Outside a test there is no such rule: an install
// trigger, an `execute` entry point or an ordinary codeunit may start a session freely.
//
// The runner gets that right by construction — BcRuntime.InTestExecutionScope is false
// outside a test — but #2825's own report lists it as unverified, and a guarantee that rests
// on construction is precisely the one that breaks silently when the construction changes.
//
// This is an install trigger rather than a new entry point because the runner already fires
// this bundle's Subtype=Install codeunit before its tests run, inside the existing
// runner-extras CI invocation. No new step, no Base App, no extra wall clock.
//
// WHY THIS FAILS IF THE GUARD STARTS FIRING
//   The assertion reads a row that ONLY "ITS Session Worker".OnRun writes, and only when
//   StartSession actually dispatched it from the install trigger:
//     * guard fires outside a test  -> StartSession throws inside OnInstallAppPerCompany,
//                                      the install fails loudly, the row is absent;
//     * dispatch silently no-ops    -> StartSession returns true having run nothing (the
//                                      #2733 shape), the row is absent.
//   It never asserts that StartSession returned or did not throw, because both of those are
//   satisfied by the broken behaviours above.
codeunit 60718 "ITS StartSession Outside Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "ITS Assert";

    [Test]
    procedure InstallTrigger_StartSession_IsNotRefused_AndRunsTheWorker()
    var
        Marker: Record "ITS Session Marker";
    begin
        Assert.IsTrue(Marker.Get('FROM-INSTALL'),
            'the install trigger''s StartSession must have dispatched "ITS Session Worker", whose ' +
            'OnRun writes this row. An absent row means either the [Test]-only guard fired outside ' +
            'a test, or the dispatch returned without running the worker.');
        Assert.AreEqual(42, Marker."Value",
            'the row must carry the value the worker''s OnRun body wrote, not a default');
    end;
}
