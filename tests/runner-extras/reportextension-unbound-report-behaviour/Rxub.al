// Issues #4909 / #4918 -- the runner's OWN refusal where it binds a reportextension only halfway.
//
// NavReportSync.BindReportExtensions registers each reportextension's request-page extension on
// the report's request page (#4909), but not the extension's report triggers or data items: BC's
// RegisterReportExtension would need the extension's data items in the report's metadata, which
// the runner does not merge yet (#4918). So a run of such a report refuses by name. Opening the
// request page alone is unaffected, which "RXUB Tests" also pins, so the
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
