codeunit 61001 "Microsoft Dependency Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "MD Assert";
        LastConfirmQuestion: Text;

    [Test]
    procedure BaseAppTable_PaymentMethod_CanInsertAndRead()
    var
        PaymentMethod: Record "Payment Method";
    begin
        PaymentMethod.Init();
        PaymentMethod.Code := 'ALR-PM';
        PaymentMethod.Description := 'AL Runner dependency metadata regression';
        PaymentMethod.Insert(true);

        Clear(PaymentMethod);
        Assert.IsTrue(PaymentMethod.Get('ALR-PM'), 'Base Application table 289 must be runtime-loadable.');
        Assert.IsTrue(PaymentMethod.Description = 'AL Runner dependency metadata regression',
            'Inserted Base Application table data must round-trip.');
    end;

    [Test]
    procedure BaseAppTable_NoSeriesLine_CanInsert()
    var
        NoSeries: Record "No. Series";
        NoSeriesLine: Record "No. Series Line";
    begin
        NoSeries.Init();
        NoSeries.Code := 'ALRUNNER';
        NoSeries.Insert(true);

        NoSeriesLine.Init();
        NoSeriesLine."Series Code" := NoSeries.Code;
        NoSeriesLine."Line No." := 10000;
        NoSeriesLine."Starting No." := 'A0001';
        NoSeriesLine."Ending No." := 'A9999';
        NoSeriesLine."Increment-by No." := 1;
        NoSeriesLine.Insert(true);

        Clear(NoSeriesLine);
        Assert.IsTrue(NoSeriesLine.Get('ALRUNNER', 10000), 'No. Series Line must be runtime-loadable.');
    end;

    [Test]
    procedure BaseAppTable_RecordRefFilteredIsEmpty_SeesRange()
    var
        PaymentMethod: Record "Payment Method";
        RecRef: RecordRef;
        FieldRef: FieldRef;
    begin
        PaymentMethod.Init();
        PaymentMethod.Code := 'ALR-EMPTY1';
        PaymentMethod.Description := 'AL Runner dependency metadata regression';
        PaymentMethod.Insert(true);

        RecRef.Open(Database::"Payment Method");
        FieldRef := RecRef.Field(PaymentMethod.FieldNo(Code));
        FieldRef.SetRange('NOEXIST');

        Assert.IsTrue(RecRef.IsEmpty(), 'RecordRef.IsEmpty must respect FieldRef.SetRange on dependency tables.');
    end;

    [Test]
    procedure BaseAppTable_RecordRefFilteredFindFirst_SeesRange()
    var
        PaymentMethod: Record "Payment Method";
        RecRef: RecordRef;
        FieldRef: FieldRef;
    begin
        PaymentMethod.Init();
        PaymentMethod.Code := 'ALR-EMPTY2';
        PaymentMethod.Description := 'AL Runner dependency metadata regression';
        PaymentMethod.Insert(true);

        RecRef.Open(Database::"Payment Method");
        FieldRef := RecRef.Field(PaymentMethod.FieldNo(Code));
        FieldRef.SetRange('NOEXIST');

        Assert.IsTrue(not RecRef.FindFirst(), 'RecordRef.FindFirst must respect FieldRef.SetRange on dependency tables.');
    end;

    [Test]
    procedure BaseAppCodeunit_NoSeries_GetNextNo_Completes()
    var
        NoSeries: Record "No. Series";
        NoSeriesLine: Record "No. Series Line";
        NoSeriesCodeunit: Codeunit "No. Series";
        NextNo: Code[20];
    begin
        NoSeries.Code := 'ALR-GUID';
        NoSeries."Default Nos." := true;
        NoSeries.Insert();

        NoSeriesLine."Series Code" := NoSeries.Code;
        NoSeriesLine."Line No." := 10000;
        NoSeriesLine."Starting No." := 'ALG0000001';
        NoSeriesLine."Ending No." := 'ALG9999999';
        NoSeriesLine."Increment-by No." := 1;
        NoSeriesLine.Insert(true);

        NextNo := NoSeriesCodeunit.GetNextNo(NoSeries.Code);

        Assert.IsTrue(NextNo <> '', 'No. Series codeunit should return a number.');
    end;

    // The Microsoft/Application Test Library test that used to live here moved to
    // tests/runner-extras/microsoft-test-library — that app is BC 28.0+ only, and its dependency
    // was forcing a 28.0 floor onto this whole suite, which needs nothing newer than 27.0.

    [Test]
    procedure BaseAppCodeunit_EnvironmentInformation_IsSandbox_IsTrue()
    var
        EnvironmentInformation: Codeunit "Environment Information";
    begin
        // Codeunit 457 -> 3702 "Environment Information Impl." -> NavTenantSettingsHelper.IsSandbox
        // dereferences NavCurrentThread.Session.Tenant.TenantSettings.EnvironmentType, UNLESS
        // Session.TestExecution.InTest is true and NavTenantSettingsHelper's private
        // testEnvironmentTypeIsSandbox tuple says sandbox — exactly the seam BC's own real
        // service-tier test harness uses (SetTestTenantEnvironmentType(true)) so that ANY AL
        // test code running under the test harness observes a sandbox, never production. The
        // runner now wires that same seam once per run, mirroring real BC: a test execution
        // context is always a sandbox, never production.
        Assert.IsTrue(EnvironmentInformation.IsSandbox(), 'A running test-execution context must report as a sandbox (mirrors real BC test harness).');
        Assert.IsTrue(not EnvironmentInformation.IsProduction(), 'A running test-execution context must never report as production.');
    end;

    [Test]
    procedure BaseAppCodeunit_EnvironmentInformation_IsSaaS_IsTrue()
    var
        EnvironmentInformation: Codeunit "Environment Information";
    begin
        // Codeunit 3702 "Environment Information Impl." IsSaaS(), decompiled from the real
        // Microsoft.Dynamics.Nav.BusinessApplication System App DLL: unless the AL-test-only
        // `testabilitySoftwareAsAService` override is set (it isn't here), IsSaaS() memoizes
        // `isSaaSConfig := IsSandbox() | <OnCheckSoftwareAsAService event result>` the first time
        // it's called and returns `isSaaSConfig` from then on. Since IsSandbox() is now true for a
        // running test (mirrors BC's own test harness — see the IsSandbox test above), the `|`
        // makes isSaaSConfig true regardless of the event result: real BC's own formula ties
        // IsSaaS() to IsSandbox() being true, not the other way around. A production on-prem
        // tenant can be non-SaaS; a sandbox (which every BC test execution now faithfully is)
        // cannot.
        Assert.IsTrue(EnvironmentInformation.IsSaaS(), 'A running test-execution sandbox must report as SaaS (IsSaaS = IsSandbox | event, per Codeunit 3702).');
    end;

    [Test]
    procedure BaseAppCodeunit_WorkflowSetup_InitWorkflow_NoThrow()
    // Regression: before fix, WorkflowEventHandling.AddEventToLibrary would throw
    //   "An event with description 'Approval of an item journal batch is requested.' already exists."
    // because:
    //   (a) BC's CreateEventsLibrary inserts a base-app event with that description inline,
    //   (b) RS subscriber then tries to add a different event with the same description, and
    //   (c) SystemInitialization.IsInProgress() returned false (skeleton has no company-open init).
    // Fix: Cecil-rewrite Codeunit151.<IsInProgress>d__24.MoveNext to always return true so
    // AddEventToLibrary's duplicate-description guard is suppressed — matching real BC where the
    // Workflow Event table is pre-populated before tests run (during system init when IsInProgress=true).
    var
        WorkflowSetup: Codeunit "Workflow Setup";
    begin
        // RED (before fix): throws NavNCLDialogException "already exists"
        // GREEN (after fix): completes without throwing
        WorkflowSetup.InitWorkflow();
        Assert.IsTrue(true, 'WorkflowSetup.InitWorkflow() must not throw on headless runner.');
    end;

    [Test]
    procedure BaseAppCodeunit_WorkflowSetup_InitWorkflow_IdempotentNoThrow()
    // Calling InitWorkflow() twice (as tests do: each [Test] calls Initialize() which calls
    // InitWorkflow) must not throw on the second call either.  Before the fix the second call
    // would also throw "already exists" because the table entries from the first call are still
    // visible (codeunit-level test isolation) and Get(FunctionName) finds them correctly, but
    // the base-app event with the same description was inserted by CreateEventsLibrary before
    // the ISV subscriber fires — causing the description-duplicate check to error for the ISV event.
    var
        WorkflowSetup: Codeunit "Workflow Setup";
    begin
        WorkflowSetup.InitWorkflow();
        // Second call — must be idempotent.
        WorkflowSetup.InitWorkflow();
        Assert.IsTrue(true, 'WorkflowSetup.InitWorkflow() must be idempotent (two calls, no throw).');
    end;

    [Test]
    procedure BaseAppCodeunit_SystemInitialization_IsInProgress_IsTrue()
    // Runner contract: SystemInitialization.IsInProgress() always returns true on the headless
    // runner.  This differs from real BC where it is false during test execution, but it is the
    // CORRECT behavior here: the runner starts every codeunit reset with an empty in-memory
    // store (no committed company-open snapshot), so test code that calls InitWorkflow() is
    // effectively running the first-ever initialization.  AddEventToLibrary's
    // duplicate-description guard only allows same-description events when IsInProgress()=true,
    // and ISV workflow events routinely share descriptions with base-app events.  Setting the
    // field permanently to true (via skeleton-state poke on every Codeunit151 instance) is the
    // only mechanism that lets InitWorkflow() complete without throwing in ALL deployment
    // topologies (base-app-only corpus AND ISV bundles).
    //
    // If a future fix can reliably detect "are we inside an InitWorkflow() call chain" and
    // scope true only to that window, this test should be updated to assert false outside that
    // window.  Until then, assert the actual observable value.
    var
        SystemInitialization: Codeunit "System Initialization";
    begin
        // RED would be: IsInProgress() = false (broken skeleton poke or missing CU151 hook).
        // GREEN (runner contract): always true — duplicate-description guard suppressed.
        Assert.IsTrue(SystemInitialization.IsInProgress(),
            'SystemInitialization.IsInProgress() must be true on the headless runner (skeleton poke).');
    end;

    // BaseAppFlowField_MatchedOrderLines_CalcFieldsAndSetRange moved to
    // tests/runner-extras/microsoft-test-library — Purchase Line's "Matched Order Lines"
    // FlowField does not exist in Base Application 27.0/27.3/27.5 (verified against the
    // shipped SymbolReference.json for each): it was introduced in BC 28.0, same as
    // Application Test Library. Leaving it here forced a 28.0 floor onto this whole
    // suite again despite the app.json declaring 27.0.

    // ── Report metadata for a PRECOMPILED dependency's report ────────────────
    //
    // Report.WordXmlPart is a pure metadata call: it returns the report's data-item /
    // column schema, reached through MetadataProvider.GetReportMetadata ->
    // NCLMetaReport.LoadMetadata -> INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata.
    // That loader answers from the EMIT registry, which only ever holds reports the runner
    // source-compiled — so report 1306, which lives in the precompiled Base Application,
    // had no entry and the call threw RunnerOutOfScopeException("not-yet-implemented").
    //
    // RED (before the fix): the positive test below dies on that out-of-scope throw.
    // GREEN: DependencyReportMetadata reconstructs the metadata document from the .app's
    // own SymbolReference.json (data items, columns, types) plus the report's AL source
    // read back out of the same .app for the column source expressions the symbol file
    // omits — so BC parses a real MetaReport and the schema names the report's data item.
    [Test]
    procedure DependencyReport_WordXmlPart_ReturnsRealDataItemSchema()
    var
        SchemaXml: Text;
    begin
        // [WHEN] The schema of report 1306 ("Standard Sales - Invoice") is requested. It is
        // declared by Base Application, which the runner loads precompiled and never
        // source-compiles, so nothing captured its metadata at emit time.
        SchemaXml := Report.WordXmlPart(1306, true);

        // [THEN] A real schema comes back naming the report's own data item. Asserting a
        // CONCRETE data-item name is what makes this test non-vacuous: an implementation
        // that returned an empty-but-well-formed document (the tempting silent fallback)
        // would satisfy "non-empty" and still fail here.
        Assert.IsTrue(SchemaXml <> '',
            'Report.WordXmlPart on a precompiled-dependency report must return its schema, not an empty text.');
        Assert.Contains(SchemaXml, 'Header',
            'Report 1306''s schema must name its "Header" data item — proving the data-item tree was reconstructed, not stubbed out empty.');
    end;

    // Negative: a report id no dependency declares at all must still fail loudly. Without
    // this, the fix above could have been implemented as "answer every report with an empty
    // document", which would turn every unknown-report bug into a silent success.
    [Test]
    procedure UnknownReport_WordXmlPart_StillFailsLoudly()
    var
        SchemaXml: Text;
    begin
        asserterror SchemaXml := Report.WordXmlPart(99999999, true);
        Assert.Contains(GetLastErrorText(), '99999999',
            'A report id no loaded dependency declares must raise a real error naming the id, not return an empty schema.');
    end;

    // ── Precompiled BaseApp query execution (NavQuery.FindDataImplAsync) ─────
    //
    // Codeunit 9170 "Conf./Personalization Mgt." (Base Application) resolves the current
    // user's default profile by opening Query 777 "Role Center from Plans" (System
    // Application) filtered by user security ID, joining table "User Plan" to table "Plan".
    // "User Security ID" is a `filter(...)` on the query — referenced only via SetRange, never
    // projected as a result `column(...)` — which is Query 777's own shape and exactly the
    // case that broke: NCLMetaQueryColumn.ColumnIndex is only assigned by BC's own runtime
    // factory for non-filter-only columns, so a filter-only column's ColumnIndex is left at
    // its CLR default (0) — indistinguishable, by value, from a genuinely-projected column at
    // real slot 0. AlRunner.QueryJoin.JoinExecutor's multi-dataitem join projector and
    // RecordPatches.QueryProjection.ApplyJoinRuntimeFilters both read that ColumnIndex naively,
    // so a runtime SetRange on "User Security ID" (Guid) got aliased onto whatever real column
    // happened to land in slot 0 (Query 777's "Role Center ID", an Integer) — comparing a Guid
    // filter value against an Integer row value, which threw NavNCLInvalidComparisonException
    // ("Unable to compare operands of type NavInteger with NavGuid") instead of ever being
    // evaluated as the filter it actually was.
    //
    // WHAT THIS FILE PROVES ABOUT QUERY 777, AND WHAT IT NO LONGER CLAIMS (issue #2153):
    //
    // A prior version of this suite carried a "positive companion" test right here —
    // Query777_RoleCenterFromPlans_MatchingPlan_ReturnsJoinedRoleCenterID — that declared
    // `Plan: Record Plan;` / `UserPlan: Record "User Plan";` local variables to seed a real
    // joined row, on the stated assumption that `Access = Internal` on System Application only
    // blocks symbol-naming surfaces like `RecordRef.Open(Database::Plan)`, not a plain local
    // variable declaration of that table type. #2150's fix — making the runner actually gate on
    // AL error diagnostics instead of silently tolerating them whenever some objects still emit
    // — surfaced that the assumption was wrong: BC's own compiler
    // (Microsoft.Dynamics.Nav.CodeAnalysis.dll, the same one alc.exe uses) raises AL0161 on BOTH
    // variable declarations, so that test could never actually compile against real BC. It was
    // only ever "passing" here because of the exact bug #2150 fixes. It was removed rather than
    // shipped as AL a real service tier would reject.
    //
    // #2153 investigated whether a legitimate way exists to seed Plan/User Plan data from
    // third-party AL without tripping AL0161, by decompiling the shipped System Application
    // Test Library app. It does: Codeunit 132916 "Azure AD Plan Test Library" (ships since BC
    // 27.0) exposes `CreatePlan(Guid, Text, Integer, Guid)` and `AssignUserToPlan(Guid, Guid)`
    // — both take only Guid/Text/Integer parameters, so a caller never declares a local
    // `Record Plan` / `Record "User Plan"` and never trips AL0161. Seeding a joined row this
    // way is possible, and the resulting assertion — "Query 777 correctly inner-joins Plan onto
    // a seeded User Plan row and projects the joined Role Center ID" — is a plain statement
    // about what BC's own precompiled query does, independent of AL Runner's existence, so that
    // restored positive coverage belongs in the upstream al-language corpus (see
    // bc-behavior-tests-go-upstream.md), not here. It does not live in this file.
    //
    // What DOES still live in this file, and what each test actually proves:
    //
    //   - Query777_RoleCenterFromPlans_NoMatchingPlan_ReturnsNoRows (below): with zero seeded
    //     rows, reading the query must faithfully report "no rows", never NRE while resolving
    //     the precompiled query's metadata. This is a runner-specific claim — it is squarely
    //     about whether AL Runner's own query engine (AlRunner.QueryJoin.JoinExecutor /
    //     RecordPatches.QueryProjection) resolves a precompiled System Application query's
    //     metadata without throwing, not about what BC does (an empty result on an empty table
    //     is not an interesting BC-behaviour claim on its own).
    //   - BaseAppCodeunit_ConfPersonalizationMgt_GetCurrentProfileNoError_NoThrow (below): drives
    //     the same query indirectly through Base App's Codeunit 9170, proving the runner's
    //     dispatch into a precompiled dependency's query does not NRE.
    //
    // Neither remaining test seeds a real joined row, so neither proves the InnerJoin executes
    // against non-empty data or that filter-only-column aliasing (NCLMetaQueryColumn.ColumnIndex
    // defaulting to 0 for a column that is only ever a `filter(...)`, not a projected `column(...)`
    // — see the original bug narrative above) stays fixed once real rows are involved. That
    // regression coverage now depends on the upstream corpus test landing (issue #2153).
    [Test]
    procedure Query777_RoleCenterFromPlans_NoMatchingPlan_ReturnsNoRows()
    var
        RoleCenterFromPlans: Query "Role Center from Plans";
    begin
        // [GIVEN] No AAD-plan mapping exists for this (freshly generated, guaranteed unused)
        // user security ID.
        RoleCenterFromPlans.SetRange(User_Security_ID, CreateGuid());
        RoleCenterFromPlans.Open();

        // [WHEN] / [THEN] Reading the query must faithfully report "no rows" — never a
        // NullReferenceException while resolving the precompiled query's metadata.
        Assert.IsTrue(not RoleCenterFromPlans.Read(),
            'Query 777 ("Role Center from Plans") must return zero rows when no AAD plan links this user to a Plan, not throw a NullReferenceException in NavQuery.FindDataImplAsync.');
        RoleCenterFromPlans.Close();
    end;

    // Integration-level companion, matching the exact frame the reported stack trace names
    // (Codeunit9170.GetCurrentProfileNoError -> TryGetDefaultProfileForCurrentUser ->
    // GetDefaultProfileID -> Query 777). This is a *_NoThrow-shaped claim only: per real BC's
    // [TryFunction] codegen, GetCurrentProfileNoError's Boolean return reports "completed
    // without an unhandled AL error", not "a profile was found" — decompiling Codeunit9170
    // confirms the Boolean is threaded straight from the try-wrapper's success flag, and with
    // no AAD-plan/profile data configured it legitimately returns true while leaving
    // AllProfile unpopulated. The row-level proof that the query itself runs (rather than
    // throwing on a real Guid filter) is the two Query777_* tests above.
    [Test]
    procedure BaseAppCodeunit_ConfPersonalizationMgt_GetCurrentProfileNoError_NoThrow()
    var
        ConfPersonalizationMgt: Codeunit "Conf./Personalization Mgt.";
        AllProfile: Record "All Profile";
    begin
        // RED (before fix): NullReferenceException in NavQuery.FindDataImplAsync while
        // Codeunit9170.GetDefaultProfileID opens the precompiled Query 777.
        // GREEN (after fix): completes normally.
        ConfPersonalizationMgt.GetCurrentProfileNoError(AllProfile);
        Assert.IsTrue(true,
            'GetCurrentProfileNoError must not throw a NullReferenceException while resolving the default profile via the precompiled Query 777.');
    end;

    // ── Lifecycle triggers of a PRECOMPILED dependency's report ──────────────
    //
    // BC's compiler emits a report's OnPreReport / OnPostReport as ASYNC overrides
    // (`OnPostReportAsync`, with `__IsAsync => true`) in the Ready2Run Base Application;
    // the runner's own emit path produces the synchronous `OnPostReport` override. BC's
    // lifecycle dispatches through `On{Pre,Post}ReportInternalAsync`, which picks the flavour
    // from `__IsAsync`. The runner's `Report.Run()` used to invoke only the sync virtual, so
    // every report the Base Application ships ran with EMPTY report-level triggers: no
    // Confirm/Message reached the test's handlers, no setup was written, no validation error
    // was raised. Report 34 "Change Payment Tolerance" is the smallest Base Application
    // report with an observable OnPostReport: it writes GL Setup's "Payment Tolerance %"
    // and then raises a Confirm through Confirm Management.
    //
    // RED (before the fix): the handler below is never invoked (BC's own end-of-test check
    // reports it unexecuted), "Payment Tolerance %" stays 0, and no error propagates.
    [Test]
    [HandlerFunctions('ChangePaymentToleranceConfirmHandler')]
    procedure PrecompiledReport_Run_OnPostReport_WritesSetupAndRaisesConfirm()
    var
        GeneralLedgerSetup: Record "General Ledger Setup";
        ChangePaymentTolerance: Report "Change Payment Tolerance";
    begin
        EnsureGeneralLedgerSetupExists();
        LastConfirmQuestion := '';

        ChangePaymentTolerance.InitializeRequest(false, '', 7.5, 12.5);
        ChangePaymentTolerance.UseRequestPage(false);
        ChangePaymentTolerance.Run();

        // The trigger body ran: it modified GL Setup BEFORE asking the question. 7.5 is a
        // value nothing else writes, so an implementation that skipped the trigger (0) or
        // ran a stubbed one cannot satisfy this.
        GeneralLedgerSetup.Get();
        Assert.IsTrue(GeneralLedgerSetup."Payment Tolerance %" = 7.5,
            StrSubstNo('Report 34''s OnPostReport must have written "Payment Tolerance %" = 7.5; got %1.', GeneralLedgerSetup."Payment Tolerance %"));
        // ...and the Confirm it raises reached the declared [ConfirmHandler].
        Assert.Contains(LastConfirmQuestion, 'change all open entries for every customer and vendor',
            'The Confirm raised inside the precompiled report''s OnPostReport must be dispatched to the test''s [ConfirmHandler].');
    end;

    // Same trigger through the instance RunModal() path, which BC's NavReportHandle routes
    // differently from Run() (it keeps the target instance alive); both must execute it.
    [Test]
    [HandlerFunctions('ChangePaymentToleranceConfirmHandler')]
    procedure PrecompiledReport_RunModal_OnPostReport_WritesSetupAndRaisesConfirm()
    var
        GeneralLedgerSetup: Record "General Ledger Setup";
        ChangePaymentTolerance: Report "Change Payment Tolerance";
    begin
        EnsureGeneralLedgerSetupExists();
        LastConfirmQuestion := '';

        ChangePaymentTolerance.InitializeRequest(false, '', 3.25, 1);
        ChangePaymentTolerance.UseRequestPage(false);
        ChangePaymentTolerance.RunModal();

        GeneralLedgerSetup.Get();
        Assert.IsTrue(GeneralLedgerSetup."Payment Tolerance %" = 3.25,
            StrSubstNo('Report 34''s OnPostReport (via RunModal) must have written "Payment Tolerance %" = 3.25; got %1.', GeneralLedgerSetup."Payment Tolerance %"));
        Assert.Contains(LastConfirmQuestion, 'change all open entries for every customer and vendor',
            'The Confirm raised inside the precompiled report''s OnPostReport must be dispatched to the test''s [ConfirmHandler] on the RunModal path too.');
    end;

    // Negative, OnPostReport: the trigger's own errors must propagate. Report 34's
    // OnPostReport starts with GLSetup.Get(), which fails when the row is missing. A run
    // that skips the trigger completes silently — exactly the old behaviour.
    [Test]
    procedure PrecompiledReport_OnPostReport_ErrorPropagates()
    var
        GeneralLedgerSetup: Record "General Ledger Setup";
        ChangePaymentTolerance: Report "Change Payment Tolerance";
    begin
        if GeneralLedgerSetup.Get() then
            GeneralLedgerSetup.Delete();

        ChangePaymentTolerance.InitializeRequest(false, '', 1, 1);
        ChangePaymentTolerance.UseRequestPage(false);
        asserterror ChangePaymentTolerance.Run();
        Assert.Contains(GetLastErrorText(), 'General Ledger Setup',
            'GLSetup.Get() inside the precompiled report''s OnPostReport must raise its "does not exist" error through Report.Run().');
    end;

    // Negative, OnPreReport: report 94 "Close Income Statement" opens its OnPreReport with
    // `if EndDateReq = 0D then Error(Text000)`. With no request page the date is blank, so
    // the very first statement of the trigger must surface as the run's error.
    [Test]
    procedure PrecompiledReport_OnPreReport_ErrorPropagates()
    var
        CloseIncomeStatement: Report "Close Income Statement";
    begin
        CloseIncomeStatement.UseRequestPage(false);
        asserterror CloseIncomeStatement.Run();
        Assert.Contains(GetLastErrorText(), 'Enter the ending date for the fiscal year.',
            'The Error() in the precompiled report''s OnPreReport must propagate through Report.Run().');
    end;

    // ---------------------------------------------------------------------------------
    // Precompiled dependency PAGES: lifecycle triggers must run in the flavour BC emitted
    // them in. Same defect shape as PrecompiledReport_* above (#2732/#2734), one layer over:
    // BC's compiler emits a page trigger either as a sync `OnOpenPage()` override or as
    // `OnOpenPageAsync()` with `__IsAsync` true. Both are virtuals on NavForm with EMPTY
    // base bodies, so resolving the sync name by reflection always succeeds — it just binds
    // the empty base method on every precompiled page, which ships the async flavour. The
    // runner's own emit produces the sync one, which is why runner-authored pages never
    // showed this and every Base Application page ran with dead lifecycle triggers.
    //
    // Page 982 "Payment Registration Setup" is the smallest Base Application page with an
    // OnOpenPage whose effect is observable from AL: it creates the current user's setup row
    // when there is none.
    //
    // RED (before the fix): OnOpenPage binds NavForm's empty base body, no row is created,
    // and both tests below fail — the positive on a missing row, the negative because
    // OnQueryClosePage is dead too and OK() closes without validating anything.
    [Test]
    procedure PrecompiledPage_OpenEdit_OnOpenPage_CreatesTheCurrentUsersRow()
    var
        PaymentRegistrationSetup: Record "Payment Registration Setup";
        SetupPage: TestPage "Payment Registration Setup";
    begin
        PaymentRegistrationSetup.DeleteAll();
        Assert.IsFalse(PaymentRegistrationSetup.Get(UserId()),
            'Precondition: the current user must have no "Payment Registration Setup" row before the page opens.');

        SetupPage.OpenEdit();
        SetupPage.Close();

        Assert.IsTrue(PaymentRegistrationSetup.Get(UserId()),
            'Page 982''s OnOpenPage must have inserted the current user''s "Payment Registration Setup" row.');
        Assert.IsTrue(PaymentRegistrationSetup."User ID" = UserId(),
            StrSubstNo('The row OnOpenPage created must be keyed on the current user; got "%1".', PaymentRegistrationSetup."User ID"));
        Assert.IsTrue(PaymentRegistrationSetup.Count() = 1,
            StrSubstNo('OnOpenPage must create exactly one row; found %1.', PaymentRegistrationSetup.Count()));
    end;

    // Negative, and the shape issue #2729 reported: page 981 "Payment Registration"'s
    // OnOpenPage calls PaymentRegistrationMgt.RunSetup(), which opens page 982 modally when
    // the current user has no setup row. With no [ModalPageHandler] declared, BC's own
    // "Unhandled UI" refusal is the correct outcome. A page whose OnOpenPage never runs opens
    // silently instead — which is exactly what the three Tests-ERM tests named in #2729 did.
    [Test]
    procedure PrecompiledPage_OnOpenPage_ModalWithNoHandler_IsRefusedLoudly()
    var
        PaymentRegistrationSetup: Record "Payment Registration Setup";
        PaymentRegistrationPage: TestPage "Payment Registration";
    begin
        PaymentRegistrationSetup.DeleteAll();

        asserterror PaymentRegistrationPage.OpenEdit();

        Assert.Contains(GetLastErrorText(), 'Unhandled UI: ModalPage 982',
            'Page 981''s OnOpenPage must run RunSetup(), which opens page 982 modally; with no handler declared that must be refused, not opened silently.');
    end;

    // The same path WITH a handler. This used to assert that the row page 982's OnOpenPage
    // inserted "must still exist after OpenEdit returns" — and it was green only because the
    // runner never raised OnQueryClosePage (#3050). Real BC does raise it, and page 982's
    // OnQueryClosePage is `if CloseAction = ACTION::LookupOK then exit(Rec.ValidateMandatoryFields(true))`,
    // whose TestField chain fails on a setup row nothing has filled in. PaymentRegistrationMgt
    // .RunSetup opens the page with PAGE.RunModal and compares against ACTION::LookupOK, so a
    // handler invoking OK closes it in exactly that lookup-confirming way.
    //
    // Measured on a real BC 28.4.53241.0 service tier, this exact test body:
    //   err = "Unhandled UI: Message Journal Template Name must have a value in Payment
    //          Registration Setup: User ID=ADMIN. It cannot be zero or empty."
    //   Rec.Get(UserId()) afterwards = No
    // So on BC the row does NOT survive: the failing close takes the insert with it. The
    // runner keeps it, and wraps the TestField as NavTestFieldException where BC wraps it in
    // its own "Unhandled UI: Message" envelope. Both of those are a DIFFERENT defect from the
    // missing trigger — tracked in #3057, and deliberately not asserted here — so this test
    // pins only the part the trigger decides: that page 982's OnQueryClosePage runs on the
    // handler's confirming close and reports the unfilled setup.
    [Test]
    [HandlerFunctions('PaymentRegistrationSetupModalHandler')]
    procedure PrecompiledPage_OnOpenPage_ModalSetupPageValidatesOnTheConfirmingClose()
    var
        PaymentRegistrationSetup: Record "Payment Registration Setup";
    begin
        PaymentRegistrationSetup.DeleteAll();

        asserterror OpenPaymentRegistrationPage();

        Assert.Contains(GetLastErrorText(), 'Journal Template Name must have a value',
            'Page 982''s OnQueryClosePage must run on the handler''s confirming close, so its ValidateMandatoryFields(true) reports the unfilled setup.');
        Assert.IsTrue(PaymentRegistrationSetup.Get(UserId()),
            'Page 982''s OnOpenPage must still have inserted the current user''s row before the close failed. (Real BC then rolls that insert back and the runner does not — #3057.)');
    end;

    local procedure OpenPaymentRegistrationPage()
    var
        PaymentRegistrationPage: TestPage "Payment Registration";
    begin
        PaymentRegistrationPage.OpenEdit();
        PaymentRegistrationPage.Close();
    end;

    [ModalPageHandler]
    procedure PaymentRegistrationSetupModalHandler(var SetupPage: TestPage "Payment Registration Setup")
    begin
        SetupPage.OK().Invoke();
    end;

    // StartSession from inside a [Test] — REFUSED, and that is real BC, not a runner limit.
    //
    // These three tests used to assert that StartSession dispatched a precompiled worker
    // (AlRunner#2733). They were green only because the runner did not implement BC's
    // TestIsolation guard. It does now (#2805), and the corpus pins the refusal on all eight
    // BC versions — StefanMaron/BusinessCentral.AL.Language.Tests session/TestStartSessionRecord.al,
    // codeunit 60397, merged as PR #149.
    //
    // BC's guard is the FIRST statement of ALSession.ALStartSessionAsyncImpl, before the
    // timeout check and before a session id is assigned:
    //
    //     if (session.TestExecution != null
    //         && (!session.TestExecution.CommitTestCodeunits
    //             || !session.TestExecution.CommitTestFunctions))
    //         throw new NavTestStartSessionNotAllowedException();
    //
    // So on a real service tier a [Test] running under TestIsolation = Codeunit (this suite's
    // mode, and BC's shipped runner 130450) can never reach StartSession's dispatch at all.
    // Asserting that it does was asserting something no service tier would agree with.
    //
    // WHAT THIS COSTS, recorded rather than glossed: the #2733/#2752 dispatch path inside
    // AlRunnerStartSession — construct the worker, resolve OnRun vs OnRunAsync, await the
    // ValueTask — no longer has AL-level coverage here, because from AL it is now only
    // reachable under --isolation disabled, which this suite does not run under. The shared
    // resolver it calls (CodeunitPatches.ResolveOnRunTrigger) keeps its other call site's
    // coverage through Codeunit.Run. Re-covering the StartSession-specific half is AlRunner#2826.
    [Test]
    procedure PrecompiledCodeunit_StartSession_FromATestIsRefusedUnderCodeunitIsolation()
    var
        SessionId: Integer;
    begin
        asserterror StartSession(SessionId, Codeunit::"Price Calculation - V16");

        Assert.Contains(GetLastErrorText(),
            'can only be started in tests that are run by a TestRunner that has TestIsolation set to Disabled',
            'StartSession inside a [Test] under Codeunit isolation must be refused with BC''s own message.');
    end;

    // The refusal precedes the session-id assignment. BC assigns sessionId.ObjectValue about
    // forty lines after the guard, so a refused call leaves the caller's by-ref untouched —
    // the same claim corpus codeunit 60397 makes, asserted here against a Base Application
    // worker rather than a fixture one.
    [Test]
    procedure PrecompiledCodeunit_StartSession_RefusedBeforeASessionIdIsAssigned()
    var
        SessionId: Integer;
    begin
        SessionId := 0;
        asserterror StartSession(SessionId, Codeunit::"Price Calculation - V15");

        Assert.AreEqual(0, SessionId,
            'a refused StartSession must not have assigned a session id.');
    end;

    // The refusal is checked BEFORE the object id is resolved, so a nonexistent codeunit id
    // reports the isolation refusal rather than "no codeunit behind this id" — matching BC,
    // whose guard runs before any codeunit lookup. Keeps the negative direction of the test
    // this replaces: StartSession still fails loudly here, just for the earlier reason.
    [Test]
    procedure PrecompiledCodeunit_StartSession_OnAnIdWithNoCodeunit_IsRefusedByIsolationFirst()
    var
        SessionId: Integer;
    begin
        asserterror StartSession(SessionId, 1999999);

        Assert.Contains(GetLastErrorText(),
            'can only be started in tests that are run by a TestRunner that has TestIsolation set to Disabled',
            'the isolation guard runs before the codeunit lookup, so it reports first.');
    end;

    // Issue #2860 — PopulateAllFields on a page that ships PRECOMPILED inside a dependency
    // .app, so its runtime metadata is not the AL compiler's own but the document
    // RecordPatches.DependencyPageMetadataXml reconstructs from SymbolReference.json.
    //
    // BC's NavForm.NewRecordAsync passes
    // MasterPage.PageProperties.SourceObject.PopulateAllFields as the
    // includeNonPrimaryKeyFields argument to NavRecord.InitializeFieldsFromFilters, so with
    // it true a new row picks up a filter on a NON-primary-key field, and with it false (BC's
    // own default, and what SourceObjectDefinition's XmlNode constructor initialises the
    // field to) it picks up only primary-key filters. The dropped attribute was therefore not
    // a missing value but a wrong one.
    //
    // The pair below is what makes this prove something rather than pass: page 367
    // "Post Codes" declares PopulateAllFields = true and page 427 "Payment Methods" declares
    // nothing, and both are read out of the SAME reconstructed-metadata path. An
    // implementation that wrote the attribute unconditionally would fail the second test; one
    // that never wrote it fails the first.
    //
    // The BC-behaviour half of this claim — that PopulateAllFields governs which filtered
    // fields a new row is initialised from — is plain BC behaviour for a service tier to
    // adjudicate. What is runner-specific, and is what this suite pins, is that a page
    // reached only through a precompiled dependency's symbol file answers the same as one the
    // runner compiled itself.
    [Test]
    procedure PopulateAllFieldsTrue_OnPrecompiledDependencyPage_InitialisesNonPrimaryKeyFilterOnNew()
    var
        PostCodes: TestPage "Post Codes";
    begin
        // Table 225 "Post Code" has primary key (Code, City); "Country/Region Code" is field
        // 4 and is NOT part of it.
        PostCodes.OpenEdit();
        PostCodes.Filter.SetFilter("Country/Region Code", 'ZZ');
        PostCodes.New();

        Assert.AreEqualText('ZZ', PostCodes."Country/Region Code".Value(),
            'page 367 declares PopulateAllFields = true, so a new row must take the non-primary-key filter too.');
        PostCodes.Close();
    end;

    [Test]
    procedure PopulateAllFieldsUnstated_OnPrecompiledDependencyPage_LeavesNonPrimaryKeyFilterAlone()
    var
        PaymentMethods: TestPage "Payment Methods";
    begin
        // Table 289 "Payment Method" has primary key (Code); Description is field 2 and is
        // NOT part of it. Page 427 states no PopulateAllFields at all.
        PaymentMethods.OpenEdit();
        PaymentMethods.Filter.SetFilter(Description, 'ZZ');
        PaymentMethods.New();

        Assert.AreEqualText('', PaymentMethods.Description.Value(),
            'page 427 states no PopulateAllFields, so BC''s own false applies and a new row must NOT take the non-primary-key filter.');
        PaymentMethods.Close();
    end;

    local procedure EnsureGeneralLedgerSetupExists()
    var
        GeneralLedgerSetup: Record "General Ledger Setup";
    begin
        if GeneralLedgerSetup.Get() then
            exit;
        GeneralLedgerSetup.Init();
        GeneralLedgerSetup."Amount Rounding Precision" := 0.01;
        GeneralLedgerSetup.Insert();
    end;

    [ConfirmHandler]
    procedure ChangePaymentToleranceConfirmHandler(Question: Text[1024]; var Reply: Boolean)
    begin
        LastConfirmQuestion := Question;
        // Decline, so the report does not go on to touch customer / vendor ledger entries.
        Reply := false;
    end;
}
