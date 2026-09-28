// Issues #4909 / #4918 -- the runner's OWN refusal where it binds a reportextension only halfway.
//
// NavReportSync.BindReportExtensions registers each reportextension's request-page extension on
// the report's request page (#4909), but not the extension's report triggers or data items: BC's
// RegisterReportExtension would need the extension's data items in the report's metadata, which
// the runner does not merge yet (#4918). So a run of such a report refuses by name. Opening the
// request page alone is unaffected, and so is running a report whose extensions declare nothing
// left unbound (empty, procedure/global-only, request-page-only): "RXUB Tests" pins both, so the
// refusal cannot drift into refusing every report that has an extension.

table 66500 "RXUB Rec"
{
    DataClassification = CustomerContent;
    fields { field(1; "Code"; Code[20]) { } }
    keys { key(PK; "Code") { Clustered = true; } }
}

report 66500 "RXUB Report"
{
    ProcessingOnly = true;
    dataset { dataitem(RxubItem; "RXUB Rec") { } }
    requestpage
    {
        layout { area(Content) { field(BaseCtl; BaseValue) { ApplicationArea = All; } } }
    }
    var BaseValue: Text[30];
}

reportextension 66500 "RXUB Report Ext" extends "RXUB Report"
{
    requestpage
    {
        layout { addlast(Content) { field(ExtCtl; ExtValue) { ApplicationArea = All; } } }
    }
    trigger OnPreReport()
    begin
        ExtValue := 'EXT-PRE';
    end;
    var ExtValue: Text[30];
}

// Reports whose extensions declare nothing the runner leaves unbound: each must still RUN to
// completion. Each report's own OnPostReport inserts a marker row the test reads back.
report 66501 "RXUB Empty Target"
{
    ProcessingOnly = true;
    dataset { dataitem(RxubItem; "RXUB Rec") { } }
    trigger OnPostReport()
    var Marker: Record "RXUB Rec";
    begin
        Marker.Code := 'RAN-66501';
        Marker.Insert();
    end;
}

reportextension 66501 "RXUB Empty Ext" extends "RXUB Empty Target"
{
}

report 66504 "RXUB Procedure Target"
{
    ProcessingOnly = true;
    dataset { dataitem(RxubItem; "RXUB Rec") { } }
    trigger OnPostReport()
    var Marker: Record "RXUB Rec";
    begin
        Marker.Code := 'RAN-66504';
        Marker.Insert();
    end;
}

reportextension 66502 "RXUB Procedure Ext" extends "RXUB Procedure Target"
{
    procedure ExtHelper(): Text
    begin
        exit(ExtGlobal);
    end;
    var ExtGlobal: Text[30];
}

report 66505 "RXUB ReqPage Target"
{
    ProcessingOnly = true;
    dataset { dataitem(RxubItem; "RXUB Rec") { } }
    requestpage
    {
        layout { area(Content) { field(BaseCtl; BaseValue) { ApplicationArea = All; } } }
    }
    trigger OnPostReport()
    var Marker: Record "RXUB Rec";
    begin
        Marker.Code := 'RAN-66505';
        Marker.Insert();
    end;
    var BaseValue: Text[30];
}

reportextension 66503 "RXUB ReqPage Ext" extends "RXUB ReqPage Target"
{
    requestpage
    {
        layout
        {
            addlast(Content) { field(ExtCtl; ExtValue) { ApplicationArea = All; } }
            modify(BaseCtl) { Caption = 'Base (modified)'; }
        }
    }
    var ExtValue: Text[30];
}

codeunit 66502 "RXUB Assert"
{
    procedure ExpectedError(Fragment: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) = 0 then
            Error('Expected error containing ''%1'' but got ''%2''', Fragment, GetLastErrorText());
    end;
}

codeunit 66503 "RXUB Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RXUB Assert";
        Seen: Text;

    [Test]
    [HandlerFunctions('OkHandler')]
    procedure ReportRun_WithAnExtensionReportTrigger_IsRefusedByName()
    begin
        asserterror Report.Run(Report::"RXUB Report", true);
        Assert.ExpectedError('out-of-scope:');
        Assert.ExpectedError('reportextension(s) 66500');
        Assert.ExpectedError('#4918');
    end;

    [Test]
    [HandlerFunctions('ReadExtHandler')]
    procedure RunRequestPage_WithAnExtensionReportTrigger_StillOpensTheRequestPage()
    var
        Parameters: Text;
    begin
        Seen := 'unset';
        Parameters := Report.RunRequestPage(Report::"RXUB Report");
        if Seen <> '' then
            Error('the handler must read the extension field (empty), got <%1>', Seen);
    end;

    [Test]
    procedure ReportRun_WithAnEmptyExtension_RunsToCompletion()
    var Marker: Record "RXUB Rec";
    begin
        Report.Run(Report::"RXUB Empty Target", false);
        if not Marker.Get('RAN-66501') then
            Error('report 66501 did not run to its OnPostReport');
    end;

    [Test]
    procedure ReportRun_WithAProcedureAndGlobalOnlyExtension_RunsToCompletion()
    var Marker: Record "RXUB Rec";
    begin
        Report.Run(Report::"RXUB Procedure Target", false);
        if not Marker.Get('RAN-66504') then
            Error('report 66504 did not run to its OnPostReport');
    end;

    [Test]
    [HandlerFunctions('SetExtOkHandler')]
    procedure ReportRun_WithARequestPageOnlyExtension_RunsToCompletion()
    var Marker: Record "RXUB Rec";
    begin
        Report.Run(Report::"RXUB ReqPage Target", true);
        if not Marker.Get('RAN-66505') then
            Error('report 66505 did not run to its OnPostReport');
    end;

    [RequestPageHandler]
    procedure SetExtOkHandler(var RequestPage: TestRequestPage "RXUB ReqPage Target")
    begin
        RequestPage.ExtCtl.SetValue('X');
        RequestPage.OK().Invoke();
    end;

    [RequestPageHandler]
    procedure OkHandler(var RequestPage: TestRequestPage "RXUB Report")
    begin
        RequestPage.OK().Invoke();
    end;

    [RequestPageHandler]
    procedure ReadExtHandler(var RequestPage: TestRequestPage "RXUB Report")
    begin
        Seen := RequestPage.ExtCtl.Value();
        RequestPage.Cancel().Invoke();
    end;
}
