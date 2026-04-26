codeunit 59983 "Test Report Handler"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        HandlerInvoked: Boolean;

    [Test]
    [HandlerFunctions('TestReportStaticRunHandler')]
    procedure TestStaticReportRun()
    var
        Logic: Codeunit "Report Handler Logic";
    begin
        // [GIVEN] A ReportHandler is registered
        HandlerInvoked := false;

        // [WHEN] We call Report.Run statically
        Logic.RunReport();

        // [THEN] The handler was invoked
        Assert.IsTrue(HandlerInvoked, 'ReportHandler should have been invoked for Report.Run');
    end;

    [Test]
    [HandlerFunctions('TestReportStaticRunHandler')]
    procedure TestStaticReportRunModal()
    var
        Logic: Codeunit "Report Handler Logic";
    begin
        HandlerInvoked := false;
        Logic.RunReportModal();
        Assert.IsTrue(HandlerInvoked, 'ReportHandler should have been invoked for Report.RunModal');
    end;

    [Test]
    [HandlerFunctions('TestReportStaticRunHandler')]
    procedure TestVarReportRun()
    var
        Logic: Codeunit "Report Handler Logic";
    begin
        HandlerInvoked := false;
        Logic.RunReportVar();
        Assert.IsTrue(HandlerInvoked, 'ReportHandler should have been invoked for variable Report.Run');
    end;

    [Test]
    [HandlerFunctions('TestReportStaticRunHandler')]
    procedure TestVarReportRunModal()
    var
        Logic: Codeunit "Report Handler Logic";
    begin
        HandlerInvoked := false;
        Logic.RunReportVarModal();
        Assert.IsTrue(HandlerInvoked, 'ReportHandler should have been invoked for variable Report.RunModal');
    end;

    [Test]
    procedure TestReportRunWithoutHandler()
    var
        Logic: Codeunit "Report Handler Logic";
    begin
        // [GIVEN] No ReportHandler is registered
        HandlerInvoked := false;

        // [WHEN] Report.Run is called without a handler
        Logic.RunReportVar();

        // [THEN] The report runs silently and no handler was dispatched
        Assert.IsFalse(HandlerInvoked, 'HandlerInvoked should remain false when no handler is registered');
    end;

    [Test]
    [HandlerFunctions('RequestPageFlagHandler')]
    procedure TestReportUseRequestPage()
    var
        Logic: Codeunit "Report Handler Logic";
    begin
        // [GIVEN] A RequestPageHandler is registered
        HandlerInvoked := false;

        // [WHEN] UseRequestPage(false) is set before Run
        Logic.RunReportVarUseRequestPage();

        // [THEN] The RequestPageHandler should NOT be invoked
        Assert.IsFalse(HandlerInvoked, 'RequestPageHandler should not be invoked when UseRequestPage is false');
    end;

    [ReportHandler]
    procedure TestReportStaticRunHandler(var TestRequestPage: TestRequestPage "Test Report Handler")
    begin
        HandlerInvoked := true;
    end;

    [RequestPageHandler]
    procedure RequestPageFlagHandler(var TestRequestPage: TestRequestPage "Test Report Handler")
    begin
        HandlerInvoked := true;
    end;
}
