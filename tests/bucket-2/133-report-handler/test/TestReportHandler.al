codeunit 59960 "Test Report Handler"
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
        // [WHEN] Report.Run is called without a handler
        // [THEN] It should not crash (report runs silently)
        Logic.RunReportVar();
    end;

    [Test]
    procedure TestReportUseRequestPage()
    var
        Logic: Codeunit "Report Handler Logic";
    begin
        // [WHEN] UseRequestPage(false) is set
        // [THEN] Run should succeed without needing a RequestPageHandler
        Logic.RunReportVarUseRequestPage();
    end;

    [ReportHandler]
    procedure TestReportStaticRunHandler(var TestRequestPage: TestRequestPage "Test Report Handler")
    begin
        HandlerInvoked := true;
    end;
}
