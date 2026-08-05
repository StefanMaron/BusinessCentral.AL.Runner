codeunit 61001 "Microsoft Dependency Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "MD Assert";

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
}
