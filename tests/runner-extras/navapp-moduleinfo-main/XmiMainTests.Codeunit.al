/// <summary>
/// Per-module NavApp.GetCurrentModuleInfo/GetCallerModuleInfo/GetModuleInfo.
/// A dependency's code must see ITS OWN module identity (the SPBLIC
/// CheckSupportedVersion install pattern); the bundle must see its own; the
/// caller-module lookup inside the dep must name this bundle.
/// </summary>
codeunit 61240 "XMI Main Tests"
{
    Subtype = Test;

    [Test]
    procedure DepGetCurrentModuleInfo_ReturnsDepOwnVersion()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.OwnVersion() <> '25.8.43.0' then
            Error('Dep must see its OWN version 25.8.43.0, got %1.', DepApi.OwnVersion());
    end;

    [Test]
    procedure DepGetCurrentModuleInfo_ReturnsDepOwnName()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.OwnName() <> 'NavAppModuleInfo Dep' then
            Error('Dep must see its OWN name, got %1.', DepApi.OwnName());
    end;

    [Test]
    procedure BundleGetCurrentModuleInfo_ReturnsBundleIdentity()
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Info);
        if Info.Name() <> 'NavAppModuleInfo Main' then
            Error('Bundle must see its own name, got %1.', Info.Name());
        if Format(Info.AppVersion()) <> '1.0.0.0' then
            Error('Bundle must see its own version 1.0.0.0, got %1.', Format(Info.AppVersion()));
    end;

    [Test]
    procedure DepGetCallerModuleInfo_NamesTheBundle()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.CallerName() <> 'NavAppModuleInfo Main' then
            Error('Caller module inside the dep must be this bundle, got %1.', DepApi.CallerName());
    end;

    /// <summary>
    /// THE REGRESSION. GetCallerModuleInfo must name the IMMEDIATE caller's module, even
    /// when that module is the dep's own. BC's ALGetCallerModuleInfo calls
    /// GetCallingAppId(excludeCurrentMethod: true), which skips exactly ONE method scope
    /// and then breaks on the very next stack frame — it never walks past frames that
    /// happen to belong to the same app.
    ///
    /// A runner that instead returns "the nearest frame from a DIFFERENT app" answers with
    /// this bundle here. Any dep that registers data keyed on GetCallerModuleInfo().Id()
    /// then writes one row per calling app instead of one row for itself, and a later
    /// name lookup that expects a single owner reports AMBIGUOUS — an error naming the
    /// wrong problem, far from this call.
    /// </summary>
    [Test]
    procedure DepGetCallerModuleInfo_AfterOwnHop_NamesTheDepNotTheBundle()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.CallerNameAfterOwnHop() <> 'NavAppModuleInfo Dep' then
            Error('Caller module across a SAME-APP hop must be the dep itself, got %1.',
                DepApi.CallerNameAfterOwnHop());
    end;

    /// <summary>
    /// The same hop by AppId — pins that the answer is the dep's real id and not an empty
    /// GUID, which is the shape that silently produces unusable rows.
    /// </summary>
    [Test]
    procedure DepGetCallerModuleInfo_AfterOwnHop_CarriesTheDepAppId()
    var
        DepApi: Codeunit "XMI Dep Api";
        EmptyId: Guid;
    begin
        if DepApi.CallerIdAfterOwnHop() = EmptyId then
            Error('Caller module id across a same-app hop must not be an empty GUID.');
        if DepApi.CallerIdAfterOwnHop() <> DepApi.OwnId() then
            Error('Caller module id across a same-app hop must equal the dep''s own id, got %1 vs %2.',
                DepApi.CallerIdAfterOwnHop(), DepApi.OwnId());
    end;

    [Test]
    procedure GetModuleInfo_ByDepAppId_ResolvesRegisteredDep()
    var
        Info: ModuleInfo;
    begin
        if not NavApp.GetModuleInfo('f6c0e4a8-7d3b-4a1c-8e5f-9b4d8c3a6f7e', Info) then
            Error('GetModuleInfo must resolve the loaded dependency by AppId.');
        if Format(Info.AppVersion()) <> '25.8.43.0' then
            Error('GetModuleInfo(depId) must carry the dep version, got %1.', Format(Info.AppVersion()));
    end;

    /// <summary>
    /// #2961. NavApp.GetModuleInfo for an app the runner has NOT loaded must answer the way
    /// BC's own ALGetModuleInfo does, and the two arms differ. This is the trapping arm: the
    /// boolean form compiles to DataError.TrapError, so an unresolvable id is `false` with no
    /// error raised.
    ///
    /// It is the discriminating half of the pair below. A helper that answered "installed"
    /// for every id — the shape that would make the corpus green by lying — passes
    /// GetModuleInfo_ByDepAppId_ResolvesRegisteredDep above and fails here.
    /// </summary>
    [Test]
    procedure GetModuleInfo_ByUnknownAppId_BooleanForm_ReturnsFalse()
    var
        Info: ModuleInfo;
    begin
        if NavApp.GetModuleInfo('00000000-dead-beef-0000-000000000001', Info) then
            Error('GetModuleInfo must not resolve an app id the runner never loaded.');
    end;

    /// <summary>
    /// #2961, the raising arm. Statement form compiles to DataError.RaiseError, and BC's
    /// ALGetModuleInfo throws NavAppException naming the id it could not find. Before the
    /// fix the runner's source-compiled polyfill returned false here whatever the DataError
    /// was, so the statement form left <c>Info</c> untouched and said nothing — the silent
    /// wrong answer loud-failures.md is about.
    /// </summary>
    [Test]
    // UPSTREAM FOLLOW-UP — #3293. This one test is NOT runner-specific and does not belong
    // here on the merits: "an app id that is not installed raises, and the message names it"
    // is plain BC behaviour that a service tier can adjudicate with any random GUID. The rest
    // of this suite genuinely is runner-specific (it asserts the runner's loaded-app closure
    // is what answers, and that PackageId matches the derived identity the runner stamps —
    // real BC has a publish step and the runner does not), so "the suite already sits in
    // runner-extras" is a precedent, not the structural reason bc-behavior-tests-go-upstream.md
    // asks for. The corpus covers only the POSITIVE by-id case today (TestNavApp.al:70,
    // TestNavAppExtended.al:95). #3293 tracks writing the negative case upstream and deleting
    // this one when the pin moves.
    procedure GetModuleInfo_ByUnknownAppId_StatementForm_RaisesNamingTheId()
    var
        Info: ModuleInfo;
    begin
        asserterror NavApp.GetModuleInfo('00000000-dead-beef-0000-000000000002', Info);
        if StrPos(GetLastErrorText(), 'No installed extension was found with ID') = 0 then
            Error('GetModuleInfo must raise BC''s own not-found message, got: %1', GetLastErrorText());
    end;

    /// <summary>
    /// #2961. The resolved module carries the runner's DERIVED package identity, not an echo
    /// of the app id. That matters because it is the same value the runner stamps into the
    /// app's Published Application "Package ID" / "Runtime Package ID" columns and onto its
    /// AllObj rows (#2963, #3066), so AL that reads ModuleInfo.PackageId and then looks the
    /// app up by package id finds it. Echoing the app id back would break that join, and is
    /// exactly what the source-compiled polyfill used to do.
    /// </summary>
    [Test]
    procedure GetModuleInfo_ByDepAppId_CarriesADerivedPackageIdNotTheAppId()
    var
        Info: ModuleInfo;
        EmptyId: Guid;
    begin
        if not NavApp.GetModuleInfo('f6c0e4a8-7d3b-4a1c-8e5f-9b4d8c3a6f7e', Info) then
            Error('GetModuleInfo must resolve the loaded dependency by AppId.');
        if Info.PackageId() = EmptyId then
            Error('The resolved module must carry a non-empty PackageId.');
        if Info.PackageId() = Info.Id() then
            Error('PackageId must be the derived package identity, not an echo of the AppId (%1).', Info.Id());
        if Info.Id() <> 'f6c0e4a8-7d3b-4a1c-8e5f-9b4d8c3a6f7e' then
            Error('GetModuleInfo(depId) must carry the dep AppId, got %1.', Info.Id());
        if Info.Publisher() <> 'AL Runner' then
            Error('GetModuleInfo(depId) must carry the dep publisher, got %1.', Info.Publisher());
    end;

    /// <summary>
    /// THE FIX (#1942). Before it, the source polyfill behind NavApp.GetCurrentModuleInfo
    /// was declared `void`, so the C# Roslyn compile of the emitted assembly failed with
    /// CS0023 ("operator '!' cannot be applied to operand of type 'void'") on this exact
    /// boolean-CONTEXT form -- a compile failure, not a wrong answer, which is why it went
    /// unnoticed: every existing test in this bundle used the statement form instead.
    /// Proves the boolean form now compiles, returns true, and populates the SAME bundle
    /// identity the statement-form test above already proved -- a default-constructed
    /// ModuleInfo (empty name/id, version 0.0.0.0) would fail every assertion here.
    /// </summary>
    [Test]
    procedure BundleGetCurrentModuleInfo_BooleanForm_ReturnsTrueAndBundleIdentity()
    var
        Info: ModuleInfo;
    begin
        if not NavApp.GetCurrentModuleInfo(Info) then
            Error('NavApp.GetCurrentModuleInfo must return true.');
        if Info.Name() <> 'NavAppModuleInfo Main' then
            Error('Bundle must see its own name, got %1.', Info.Name());
        if Format(Info.AppVersion()) <> '1.0.0.0' then
            Error('Bundle must see its own version 1.0.0.0, got %1.', Format(Info.AppVersion()));
        if Info.Id() <> 'a7d1f5b9-8e4c-4b2d-9f6a-0c5e9d4b7a8f' then
            Error('Bundle must see its own AppId, got %1.', Info.Id());
    end;

    /// <summary>
    /// Discriminating direction for the same fix: a broken polyfill that returns one
    /// hard-coded identity (e.g. always the bundle's) would pass the test above and fail
    /// here, or vice versa. The dep must see ITS OWN identity through the boolean form,
    /// never the consuming bundle's -- the exact per-emitted-assembly split the statement
    /// form already proves for <c>DepGetCurrentModuleInfo_ReturnsDepOwnVersion</c>.
    /// </summary>
    [Test]
    procedure DepGetCurrentModuleInfo_BooleanForm_ReturnsDepOwnIdentity()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.OwnVersionBooleanForm() <> '25.8.43.0' then
            Error('Dep must see its OWN version 25.8.43.0 through the boolean form, got %1.',
                DepApi.OwnVersionBooleanForm());
        if DepApi.OwnNameBooleanForm() <> 'NavAppModuleInfo Dep' then
            Error('Dep must see its OWN name through the boolean form, got %1.', DepApi.OwnNameBooleanForm());
        if DepApi.OwnIdBooleanForm() <> 'f6c0e4a8-7d3b-4a1c-8e5f-9b4d8c3a6f7e' then
            Error('Dep must see its OWN AppId through the boolean form, got %1.', DepApi.OwnIdBooleanForm());
    end;

    /// <summary>
    /// Boolean-form coverage for GetCallerModuleInfo. Its polyfill already returned
    /// `bool` before #1942 (unlike GetCurrentModuleInfo), so this is regression cover for
    /// a value context that was never previously exercised -- not part of the fix itself.
    /// </summary>
    [Test]
    procedure DepGetCallerModuleInfo_BooleanForm_ReturnsTrueAndNamesBundle()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.CallerNameBooleanForm() <> 'NavAppModuleInfo Main' then
            Error('Caller module inside the dep must be this bundle through the boolean form, got %1.',
                DepApi.CallerNameBooleanForm());
    end;

    /// <summary>
    /// #2961. The THREE ways AL can ask about one app must agree on that app's PackageId.
    ///
    /// In real BC they cannot disagree, because there is only one implementation:
    /// ALGetCurrentModuleInfo and ALGetCallerModuleInfo are each a two-line forward into
    /// ALGetModuleInfo, which fills PackageId from navAppRuntimeMetadata.PackageId.Value for
    /// all three. The runner patches the three entry points independently — five sites in
    /// total once the source-compiled polyfills are counted — so the agreement is a property
    /// of runner code and nothing else, and it needs a test that fails when one of the five
    /// is changed alone.
    ///
    /// This is exactly what it caught: giving the by-id lookup the runner's derived package
    /// identity while the two stack-walk patches still echoed the AppId made this bundle
    /// report two different PackageIds for ITSELF depending on which call was used. RED
    /// against that state, GREEN once all five share one constructor
    /// (NavAppModuleInfoPatches.MakeModuleInfo).
    ///
    /// It is deliberately written against the bundle's OWN id, read back from
    /// GetCurrentModuleInfo rather than hard-coded, so it stays true if the fixture's app id
    /// ever changes and so it cannot pass by comparing two constants.
    /// </summary>
    [Test]
    procedure ModuleInfo_ForOneApp_AgreesOnPackageIdAcrossEntryPoints()
    var
        Current: ModuleInfo;
        ById: ModuleInfo;
        EmptyId: Guid;
    begin
        if not NavApp.GetCurrentModuleInfo(Current) then
            Error('GetCurrentModuleInfo must resolve the executing bundle.');
        if Current.Id() = EmptyId then
            Error('The executing bundle must have a non-empty AppId.');

        if not NavApp.GetModuleInfo(Current.Id(), ById) then
            Error('GetModuleInfo must resolve the executing bundle by its own AppId %1.', Current.Id());

        // The same app, asked two ways: every identity field must match.
        if ById.Id() <> Current.Id() then
            Error('AppId disagrees across entry points: by-id %1 vs current %2.', ById.Id(), Current.Id());
        if ById.Name() <> Current.Name() then
            Error('Name disagrees across entry points: by-id %1 vs current %2.', ById.Name(), Current.Name());
        if ById.PackageId() <> Current.PackageId() then
            Error('PackageId disagrees across entry points: by-id %1 vs current %2. In BC both come from the same field.',
                ById.PackageId(), Current.PackageId());

        // ...and it is the runner's derived package identity on BOTH, not an echo of the
        // AppId. Without this, two entry points that BOTH echoed the AppId would agree and
        // pass the comparison above while still reporting a value that does not join to the
        // app's Published Application row.
        if Current.PackageId() = EmptyId then
            Error('The executing bundle must carry a non-empty PackageId.');
        if Current.PackageId() = Current.Id() then
            Error('PackageId must be the derived package identity, not an echo of the AppId (%1).', Current.Id());
    end;

    /// <summary>
    /// #2961. NavApp.GetModuleInfo(Guid.Empty) is BC's "give me the application family"
    /// branch: real BC answers it from NavGlobal.AppDatabase.SqlDatabaseProperties.
    /// ApplicationFamily, which the runner has no truthful source for, so it REFUSES loudly
    /// (.claude/rules/loud-failures.md) instead of inventing a family string.
    ///
    /// This is here because unifying the source-compiled polyfill onto the shared
    /// implementation CHANGED first-party AL behaviour: the old private copy returned plain
    /// `false` for the empty id, so AL saw "not installed" — an answer BC never gives on this
    /// branch, and one indistinguishable from a genuine miss. Refusing is the right change,
    /// and a behaviour change to first-party AL needs a test whether or not it is an
    /// improvement.
    ///
    /// asserterror DOES trap a RunnerOutOfScopeException — see the header of
    /// AlRunner/Infrastructure/RunnerOutOfScopeException.cs — so the refusal is observable
    /// from AL and the three fragments below pin the API, the reason and the marker
    /// separately rather than matching one long string.
    /// </summary>
    [Test]
    procedure GetModuleInfo_ByEmptyGuid_RefusesLoudlyInsteadOfAnsweringNotInstalled()
    var
        Info: ModuleInfo;
        EmptyId: Guid;
    begin
        asserterror NavApp.GetModuleInfo(EmptyId, Info);

        if StrPos(GetLastErrorText(), 'out-of-scope:') = 0 then
            Error('Guid.Empty must raise the runner''s out-of-scope refusal, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'NavApp.GetModuleInfo(Guid.Empty)') = 0 then
            Error('The refusal must name the API that was touched, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'not-yet-implemented') = 0 then
            Error('The reason must be not-yet-implemented — this branch is in scope, just unbuilt. Got: %1',
                GetLastErrorText());

        // NOT the not-found message: answering the empty id with "no installed extension was
        // found with ID 00000000-..." would be the silent wrong answer this replaced.
        if StrPos(GetLastErrorText(), 'No installed extension was found') > 0 then
            Error('Guid.Empty must not be reported as a not-found app id, got: %1', GetLastErrorText());
    end;
}
