// Issue #2961 — NavApp.GetModuleInfo(appId, info) must answer for apps the runner has loaded.
//
// RUNNER-MECHANISM claim. Which apps are loaded is a fact about the runner, not about AL, so
// "GetModuleInfo answers for the System Application" is only a meaningful statement here.
//
// BC's ALNavApp.ALGetModuleInfo resolves the id against
// NavCurrentThread.TryResolveAppGroup().OrderedAppMetadata. That list is empty on the skeleton
// runtime, so every id missed. Two different wrong answers followed, one per compilation route:
//
//   * A PRECOMPILED app calling the real Ncl method got BC's raise. Measured on BC 28.1:
//     Codeunit1809 "Assisted Setup Installation".OnInstallAppPerCompany asks
//     EnvironmentInformation.VersionInstalled(its own app id), which is
//     NavApp.GetModuleInfo(AppID, AppInfo), and got "No installed extension was found with ID
//     '63ca2fa4-4f03-4f2b-a480-172fef340d3f'" — the System Application's own id, for an app the
//     runner had loaded.
//   * SOURCE-COMPILED AL went through BcAssembler's polyfill redirect instead, and that copy
//     returned false on a miss whatever the DataError argument said. AL emits a bare
//     `NavApp.GetModuleInfo(id, info)` statement with DataError.ThrowError, so a miss SILENTLY
//     SUCCEEDED where a service tier raises, and the caller carried on with an empty ModuleInfo.
//
// Both routes now share one implementation, so they cannot drift apart again.
//
// The trap-versus-raise half below is plain BC behaviour rather than a runner claim, and by
// bc-behavior-tests-go-upstream.md it belongs in the al-language corpus. It is asserted here
// because it is inseparable from the runner-specific half — the same call, one argument apart —
// and because the raise is exactly what regressed to a silent success. The message text asserted
// is BC's own, copied from the 28.1 output quoted above rather than invented.
//
// Honest scope of this suite: measured against a runner built without the fix, four of the five
// tests below ALREADY PASSED, because source-compiled AL reaches the helper through the polyfill
// redirect and that copy already resolved ids it knew. Exactly one goes RED without the fix —
// GetModuleInfoById_UnknownId_StatementFormRaises, the silent-success case. The other four are
// kept because they pin what the now-shared implementation must keep answering, not because they
// prove the fix. The Cecil half is unreachable from source-compiled AL by construction, and is
// pinned instead by AlRunner.Tests/NavAppGetModuleInfoByIdTests.cs.
codeunit 65561 "NGM Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "NGM Assert";
        SystemApplicationIdTok: Label '63ca2fa4-4f03-4f2b-a480-172fef340d3f', Locked = true;

    local procedure UnknownAppId(): Guid
    var
        Unknown: Guid;
    begin
        // A well-formed id no app in the closure can have.
        Evaluate(Unknown, '{0f8d1e2c-0000-4000-8000-0f8d1e2c0000}');
        exit(Unknown);
    end;

    // The bundle's own app resolves by id, and the answer is the bundle, not some default.
    [Test]
    procedure GetModuleInfoById_OwnApp_AnswersTheSameModuleAsGetCurrentModuleInfo()
    var
        Own: ModuleInfo;
        ById: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Own);
        Assert.IsTrue(NavApp.GetModuleInfo(Own.Id(), ById),
            'GetModuleInfo must resolve the id GetCurrentModuleInfo just handed out');

        Assert.AreEqual(Own.Id(), ById.Id(), 'the resolved module must be the one asked for');
        Assert.AreEqual('Runner Extras - NavApp GetModuleInfo By Id', ById.Name(),
            'the concrete app name, not a blank default');
        Assert.AreEqual('AL Runner', ById.Publisher(), 'the concrete publisher');
        Assert.AreEqual(Own.AppVersion(), ById.AppVersion(), 'the version must match');
    end;

    // The load-bearing case: a PRECOMPILED app in the closure, resolved by id. This is the
    // question Codeunit1809 asks about itself during install, and the one that raised.
    [Test]
    procedure GetModuleInfoById_PrecompiledSystemApplication_Resolves()
    var
        SystemApp: ModuleInfo;
        SystemAppId: Guid;
    begin
        Evaluate(SystemAppId, SystemApplicationIdTok);

        Assert.IsTrue(NavApp.GetModuleInfo(SystemAppId, SystemApp),
            'the System Application is loaded, so GetModuleInfo must resolve its id');
        Assert.AreEqual(SystemAppId, SystemApp.Id(), 'the resolved module must be the System Application');
        Assert.AreEqual('Microsoft', SystemApp.Publisher(), 'the System Application''s publisher');
        Assert.IsTrue(SystemApp.AppVersion().Major() > 0,
            'a real version, not a zeroed default');
    end;

    // VersionInstalled is what Codeunit1809 actually calls, and DataVersion is what it reads.
    // BC computes DataVersion as GetDataVersionForInstall ?? GetDataVersionForUpgrade ?? Version,
    // and both helpers are gated on a session install/upgrade context targeting that same app.
    // The runner sets neither, so BC's own expression falls through to the app version — this
    // pins that the runner answers what BC's expression yields under the runner's session state,
    // rather than a zero that would send install triggers down their first-install branch.
    [Test]
    procedure GetModuleInfoById_DataVersion_MatchesTheAppVersion()
    var
        Own: ModuleInfo;
        ById: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Own);
        Assert.IsTrue(NavApp.GetModuleInfo(Own.Id(), ById), 'precondition: the id must resolve');

        Assert.AreEqual(ById.AppVersion(), ById.DataVersion(),
            'DataVersion must equal AppVersion with no install or upgrade context in the session');
    end;

    // The trapping form: an unknown id answers false and does not raise.
    [Test]
    procedure GetModuleInfoById_UnknownId_TrappingFormAnswersFalse()
    var
        Missing: ModuleInfo;
    begin
        Assert.IsFalse(NavApp.GetModuleInfo(UnknownAppId(), Missing),
            'an id no loaded app has must answer false in the trapping form');
    end;

    // The raising form: the same unknown id as a bare statement must raise BC's own error.
    // Before the fix this returned quietly and execution continued past it.
    [Test]
    procedure GetModuleInfoById_UnknownId_StatementFormRaises()
    var
        Missing: ModuleInfo;
    begin
        asserterror NavApp.GetModuleInfo(UnknownAppId(), Missing);

        Assert.ExpectedError('No installed extension was found', GetLastErrorText());
    end;
}
