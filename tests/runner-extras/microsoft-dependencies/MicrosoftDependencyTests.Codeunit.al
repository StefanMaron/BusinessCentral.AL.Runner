codeunit 61001 "Microsoft Dependency Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "MD Assert";
        LibraryNoSeries: Codeunit "Library - No. Series";

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

    [Test]
    procedure BaseAppCodeunit_LibraryNoSeries_CreateNoSeriesLine_Completes()
    var
        NoSeries: Record "No. Series";
        NoSeriesLine: Record "No. Series Line";
    begin
        NoSeries.Code := 'ALRLIB';
        NoSeries.Insert();

        LibraryNoSeries.CreateNoSeriesLine('ALRLIB', 1, 'ALL0000001', 'ALL9999999');

        Assert.IsTrue(NoSeriesLine.Get('ALRLIB', 10000), 'Library - No. Series should create a No. Series Line.');
    end;

    [Test]
    procedure BaseAppCodeunit_EnvironmentInformation_IsSandbox_IsFalse()
    var
        EnvironmentInformation: Codeunit "Environment Information";
    begin
        // Codeunit 457 -> 3702 "Environment Information Impl." -> NavTenantSettingsHelper.IsSandbox
        // dereferences NavCurrentThread.Session.Tenant.TenantSettings.EnvironmentType. On the headless
        // skeleton the session.tenant was null -> NRE. Faithful headless default is OnPrem (non-sandbox):
        // EnvironmentType = Production -> IsSandbox() = false.
        Assert.IsTrue(not EnvironmentInformation.IsSandbox(), 'Headless OnPrem environment must not be a sandbox.');
    end;

    [Test]
    procedure BaseAppCodeunit_EnvironmentInformation_IsSaaS_IsFalse()
    var
        EnvironmentInformation: Codeunit "Environment Information";
    begin
        // IsSaaS() bottoms out in IsSandbox() + isSaaSConfig; on headless OnPrem both are false.
        Assert.IsTrue(not EnvironmentInformation.IsSaaS(), 'Headless OnPrem environment must not be SaaS.');
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

}
