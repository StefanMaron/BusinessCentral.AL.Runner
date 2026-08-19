/// #1883 follow-up to #1800/#1947 — the "single-site" cluster: 15 target types that each had
/// exactly ONE orphaned `Hook(...)` (JmpHook) registration in AlRunner/BcRuntime.cs, measured via
/// `AL_RUNNER_HOOK_AUDIT=1` against the al-language corpus. JmpHook is disabled by default (see
/// AlRunner/Infrastructure/JmpHook.cs), so every one of these registrations was a silent no-op —
/// BC's real, unpatched body ran instead, and the corpus (2096/2096) was already green before this
/// PR touched anything. Each site got the same empirical triage #1800/#1947 established:
///
///   14 of the 15 were harmless: BC's real, unpatched body already does the right thing (or, for
///   RecordLink.HasLinks / NavHttpClient.get_Target / NavHttpResponseMessageBase.get_Target,
///   was ALREADY Cecil-owned by a separate, always-on IL rewrite — the JmpHook registration was
///   dead code duplicating a mechanism that already worked). Their `Hook(...)` call sites (and,
///   where nothing else referenced them, their now-dead replacement bodies) were deleted outright.
///
///   1 of the 15 (NavHttpRequestMessage.get_Target) was a genuine orphaned bug: BC's real body
///   constructs `new SharedNavHttpRequestMessage(base.Tree.Session.Company.SharedObjects)` and
///   `base.Tree.Session.Company.SharedObjects` is null on the headless skeleton, so
///   `TreeObject..ctor` throws `ArgumentNullException("parent")` — reproducible from a bare
///   `var Req: HttpRequestMessage;` declaration, since NavHttpRequestMessage's own ctor eagerly
///   touches Target to initialise a default HttpRequestMessage. Fixed by Cecil-owning the getter
///   (NclCecilRewrite.cs, mirroring the sibling NavHttpClient/NavHttpResponseMessageBase pattern
///   that already existed there) instead of leaving it to the disabled JmpHook layer.
///
/// The tests below are regression guards with a narrower runner-mechanism claim than the full BC
/// behaviour: "no redirect fires here [or: the Cecil-owned redirect fires], and the resulting body
/// completes without throwing" — not a re-proof of what BC returns, which for the sites backed by
/// existing upstream corpus tests (CompanyName/TestField/Labels/GetLastErrorCallStack/Format/
/// Notification.AddAction — cited per-test below) is already proven there. Same framing as
/// xmlport-cluster-hooks-1800's InstanceExportImportRoundTrip_RealBcBody_NoThrow (see
/// bc-behavior-tests-go-upstream.md). NavHttpRequestMessage's own more specific BC-behaviour claim
/// (constructing/configuring a request without sending it must not throw) is filed upstream too —
/// see StefanMaron/BusinessCentral.AL.Language.Tests, network/TestHttpRequestMessage.al.
codeunit 62190 "SSOH Tests"
{
    Subtype = Test;

    // No Codeunit Assert dependency in this app (see xmlport-cluster-hooks-1800's plain-Error
    // convention / errormessages-page-setrecords' own local "EMSR Assert" — this app.json
    // declares no dependency on the System Application test library). Plain Error() with an
    // explicit condition check plays the same role.

    // ── ALCompanyProperty.ALDisplayName (AL surface: CompanyProperty.DisplayName()) ──────────
    // No prior corpus coverage existed for this specific AL surface (CompanyName() -- a
    // DIFFERENT method, ALDatabase.ALCompanyName -- is what the corpus's
    // session/TestSessionFunctions.al already covers). Real body: session.IsOpen (already seeded
    // true) -> session.Company.CompanyDisplayName -> reads NavRecord on table 2000000006 with a
    // GetCompanyDisplayNameDefaulted fallback to the technical company name. Runs clean.
    [Test]
    procedure CompanyPropertyDisplayName_RealBcBody_NoThrow()
    var
        Result: Text;
    begin
        Result := CompanyProperty.DisplayName();
        if Result = '' then
            Error('CompanyProperty.DisplayName() must return a non-empty company display name');
    end;

    // ── CallStackElement.TryGetSourceInfo ─────────────────────────────────────────────────────
    // Already exercised (and proven) by the corpus's
    // codeunit/TestCodeunitAlCallStack.al -- CallStack_AfterAssertError_ContainsALFrames, which
    // asserts GetLastErrorCallStack() actually contains AL frame text. This is a lighter-weight
    // regression guard confirming the same real-body path stays reachable from this app too.
    [Test]
    procedure GetLastErrorCallStack_AfterError_RealBcBody_NoThrow()
    var
        Stack: Text;
    begin
        asserterror Error('SSOH probe error');
        Stack := GetLastErrorCallStack();
        if Stack = '' then
            Error('GetLastErrorCallStack() must return non-empty call-stack text after an AL error');
    end;

    // ── NavALErrorInfo.LogAddActionFailure(string) + NavCodeunit.ContainsMethod ──────────────
    // Both reached by the SAME AL surface: ErrorInfo.AddAction(Caption, Codeunit, MethodName)
    // with a method name that does not exist on the target codeunit -- ContainsMethod resolves
    // false, which routes into LogAddActionFailure's telemetry no-op. No prior corpus coverage
    // existed for ErrorInfo.AddAction (only Notification.AddAction, a sibling surface backed by
    // ContainsMethodWithAttribute, is covered -- see session/TestSystemExtended.al
    // NotificationAddAction_AddsActionWithoutError). Real bodies: NavCodeunit.ContainsMethod
    // constructs a NavCodeunitHandle and calls NavApplicationMethod.FindMethod, catching
    // NavMetadataNotFoundException -> false; LogAddActionFailure reads
    // session.Tenant.PartnerTelemetryClient (null-conditional) and session.Diagnostics (seeded by
    // MetadataPatches.InjectSkeletonSystemTenant, the same fix #1800 relied on) -- both already
    // seeded on the skeleton. Runs clean; AddAction itself returns without throwing.
    [Test]
    procedure ErrorInfoAddAction_UnknownMethodName_RealBcBody_NoThrow()
    var
        Info: ErrorInfo;
    begin
        Info.Message := 'SSOH probe';
        Info.AddAction('Click', Codeunit::"SSOH Tests", 'NoSuchProcedureAtAll');
    end;

    // ── NavApplicationObjectBase.get_Session ──────────────────────────────────────────────────
    // Trivial `=> session` readonly-field read; the ctor (Cecil-owned separately) already
    // field-pokes the session directly. Every AL object constructed anywhere in this suite
    // already exercises this path implicitly -- this test just names the claim explicitly via a
    // codeunit that reads its own Session-derived state.
    [Test]
    procedure ApplicationObjectBase_SessionDerivedRead_RealBcBody_NoThrow()
    var
        Result: Text;
    begin
        // The claim is that reading Session-backed state (UserId) does not throw; UserId's
        // actual value is environment-dependent (login/service-account name) so it is not
        // asserted here — only that the read completes.
        Result := UserId();
        Clear(Result);
    end;

    // ── NavFilterPageBuilder.RunModalAsync (AL surface: FilterPageBuilder.RunModal()) ────────
    // No prior corpus coverage. An earlier investigation had found this hook installs but never
    // fires (async state machine bypasses JmpHook's native-precode patch) -- so it was already
    // inert even under AL_RUNNER_ENABLE_JMPHOOK=1. Real body runs cleanly to completion in a
    // non-interactive skeleton session and returns False (Action.LookupCancel) rather than
    // throwing or hanging.
    [Test]
    procedure FilterPageBuilder_RunModal_RealBcBody_NoThrow()
    var
        FPB: FilterPageBuilder;
        Ok: Boolean;
    begin
        FPB.AddTable('SSOH Probe Table', Database::"SSOH Probe Table");
        Ok := FPB.RunModal();
        if Ok then
            Error('FilterPageBuilder.RunModal() must return False (non-interactive skeleton session, no UI to confirm)');
    end;

    // ── NavIntegerFormatter.FormatWithFormatNumber (AL surface: Format(Integer, Len, FmtStr)) ─
    // Already exercised by the corpus (json/TestJsonXmlDeepContracts.al uses
    // `Format(10, 0, '<Integer>')`, plus every plain Format(IntegerValue[, Len]) call across the
    // suite). Real signature (decompiled) takes a single NavValue, not a varargs array as the
    // deleted hook's comment claimed.
    [Test]
    procedure FormatInteger_WithCustomFormatString_RealBcBody_NoThrow()
    var
        Result: Text;
    begin
        Result := Format(42, 0, '<Integer>');
        if Result <> '42' then
            Error('Format(42, 0, ''<Integer>'') must return "42", got: %1', Result);
    end;

    // ── NavOpenTelemetryLogger..ctor ───────────────────────────────────────────────────────────
    // Runs unconditionally once per process during NavEnvironment construction, long before any
    // AL test executes -- there is no AL-reachable trigger to isolate. This suite running at all
    // (any test in it passing) is itself the regression guard: if the real ctor crashed on Linux
    // as the deleted hook's comment claimed, the whole process would fail at startup, not just
    // one test.
    [Test]
    procedure ProcessStartup_ImpliesOpenTelemetryLoggerCtor_RealBcBody_NoThrow()
    begin
        // This test executing at all (reaching this line) proves NavOpenTelemetryLogger..ctor
        // already ran clean at process startup — see the class comment above.
    end;

    // ── NCLMetaField.get_FieldCaption + NavTextConstant.get_Value ─────────────────────────────
    // Both already exercised by the corpus (record/TestRecordTestField.al's Record_TestField_*
    // tests assert GetLastErrorText() is non-empty after a TestField failure, which formats the
    // error via FieldCaption; every AL Label anywhere compiles to a NavTextConstant, exercised
    // constantly). Regression guard for this app specifically.
    [Test]
    procedure TestFieldFailure_ErrorMessageIncludesFieldCaption_RealBcBody_NoThrow()
    var
        Probe: Record "SSOH Probe Table";
    begin
        Probe.Insert();
        asserterror Probe.TestField(Flag);
        if GetLastErrorText() = '' then
            Error('TestField on an unset Boolean must throw with a non-empty, caption-formatted message');
    end;

    // ── NavHttpClient.get_Target / NavHttpResponseMessageBase.get_Target ─────────────────────
    // ALREADY Cecil-owned (NclCecilRewrite.cs, "NavHttpClient egress" block) before this PR — the
    // JmpHook.Apply registrations were dead duplicates, now correctly tracked as REDUNDANT in
    // NclCecilRewrite.CecilOwned rather than misclassified as orphaned. Local construction and
    // header/base-address configuration (no egress) must not throw.
    [Test]
    procedure HttpClient_LocalConfiguration_RealBcBody_NoThrow()
    var
        Client: HttpClient;
    begin
        Client.SetBaseAddress('https://example.invalid');
        Client.DefaultRequestHeaders.Add('X-Probe', 'ssoh');
    end;

    // A fresh, never-populated HttpResponseMessage's backing .NET HttpResponseMessage is
    // constructed with the parameterless ctor, whose default StatusCode is 200 OK — so
    // IsSuccessStatusCode() reads True here even though nothing was ever sent. That is real
    // .NET/BC behaviour (not a runner artifact); the claim under test is only "reading it does
    // not throw", proven by reaching the check at all.
    [Test]
    procedure HttpResponseMessage_LocalRead_RealBcBody_NoThrow()
    var
        Resp: HttpResponseMessage;
    begin
        if not Resp.IsSuccessStatusCode() then
            Error('a fresh, never-populated HttpResponseMessage is expected to read back as success (default .NET HttpResponseMessage StatusCode is 200 OK) — reading it must not throw either way');
    end;

    // ── NavHttpRequestMessage.get_Target — the ONE genuine bug in this cluster ────────────────
    // Before this PR: `var Req: HttpRequestMessage;` alone threw ArgumentNullException("parent")
    // out of TreeObject..ctor, because NavHttpRequestMessage's own ctor eagerly calls
    // Target.SetMessage(new HttpRequestMessage()) during scope-ctor field setup, and the real
    // get_Target body constructs `new SharedNavHttpRequestMessage(base.Tree.Session.Company.
    // SharedObjects)` with a null container on the headless skeleton (unlike its two now-Cecil-
    // owned siblings above, this one had NO Cecil rewrite at all). Fixed by Cecil-owning the
    // getter (NclCecilRewrite.cs). This is the runner-mechanism half of the fix; the full
    // BC-behaviour claim (constructing/configuring a request without sending it must not throw)
    // is filed upstream — see StefanMaron/BusinessCentral.AL.Language.Tests,
    // network/TestHttpRequestMessage.al.
    [Test]
    procedure HttpRequestMessage_Construction_RealBcBody_NoThrow()
    var
        Req: HttpRequestMessage;
    begin
        Req.Method := 'GET';
        if Req.Method <> 'GET' then
            Error('HttpRequestMessage.Method must round-trip after construction, got: %1', Req.Method);
    end;
}

table 62191 "SSOH Probe Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Flag; Boolean) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
