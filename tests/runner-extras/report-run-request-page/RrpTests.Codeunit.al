/// <summary>
/// Report.RunRequestPage under test must reach the test's own [RequestPageHandler].
///
/// Running a request page for a HUMAN needs a client; running one under test does
/// not — BC's NavForm.RunModalAsync consults NavTestExecution.TestHandleModalForm
/// FIRST, which builds a NavTestRequestPage and invokes the declared handler, and
/// only falls through to the client callback when no handler matched. AL that calls
/// RunRequestPage to capture a report's RequestPageParameters XML is therefore
/// ordinary in-scope AL.
///
/// Both directions are pinned:
///   * handler confirms with OK  -> the handler body ran, the request page really
///     opened (its OnOpenPage logged), and the data-item filter the handler set survives
///     into the returned parameters XML;
///   * handler cancels           -> BC returns an empty parameters string, while the
///     handler still demonstrably ran, so "cancelled" is distinguishable from "never ran".
///
/// NOT claimed here: the request page's own OnOpenPage trigger. It does not fire under the
/// runner (issue: request-page form initialisation is gated off), and asserting it would
/// make this suite fail for a reason unrelated to handler dispatch.
/// </summary>
codeunit 62012 "RRP Tests"
{
    Subtype = Test;

    local procedure Seed()
    var
        Row: Record "RRP Row";
        LogRec: Record "RRP Log";
    begin
        LogRec.DeleteAll();
        Row.DeleteAll();
        Row.Init();
        Row."Entry No." := 1;
        Row.Name := 'first';
        Row.Insert();
        Row.Init();
        Row."Entry No." := 2;
        Row.Name := 'second';
        Row.Insert();
    end;

    [Test]
    [HandlerFunctions('ConfirmingRequestPageHandler')]
    procedure RunRequestPage_RunsTheHandlerAndReturnsTheFilterItSet()
    var
        LogRec: Record "RRP Log";
        Parameters: Text;
    begin
        Seed();

        Parameters := Report.RunRequestPage(Report::"RRP Request Page Report");

        if LogRec.MarkerCount('rp-handler') <> 1 then
            Error('The [RequestPageHandler] never ran: expected exactly 1 rp-handler log row, got %1.',
                LogRec.MarkerCount('rp-handler'));
        if Parameters = '' then
            Error('RunRequestPage returned an empty parameters string after the handler confirmed with OK.');
        if StrPos(Parameters, 'ReportParameters') = 0 then
            Error('Expected a ReportParameters document, got: %1', Parameters);
        // BC serialises a data item's view with useCaptions:false, so the handler's filter on
        // "Entry No." (field 1) appears as WHERE(Field1=1(1)) — the field NUMBER, not its caption.
        if StrPos(Parameters, 'DataItem name="Rows"') = 0 then
            Error('The Rows data item is missing from the parameters: %1', Parameters);
        if StrPos(Parameters, 'WHERE(Field1=1') = 0 then
            Error('The filter the handler set did not survive into the parameters: %1', Parameters);
    end;

    [Test]
    [HandlerFunctions('CancellingRequestPageHandler')]
    procedure RunRequestPage_CancelledHandlerReturnsNoParameters()
    var
        LogRec: Record "RRP Log";
        Parameters: Text;
    begin
        Seed();

        Parameters := Report.RunRequestPage(Report::"RRP Request Page Report");

        if LogRec.MarkerCount('rp-cancel') <> 1 then
            Error('The cancelling [RequestPageHandler] never ran: expected exactly 1 rp-cancel log row, got %1.',
                LogRec.MarkerCount('rp-cancel'));
        if Parameters <> '' then
            Error('A cancelled request page must yield no parameters, got: %1', Parameters);
    end;

    [RequestPageHandler]
    procedure ConfirmingRequestPageHandler(var RequestPage: TestRequestPage "RRP Request Page Report")
    var
        LogRec: Record "RRP Log";
    begin
        LogRec.Log('rp-handler');
        // Stands in for the user narrowing the report: filter the Rows data item to a single
        // entry. BC serialises every data item's record view into the parameters XML, so this
        // filter is exactly what the caller of RunRequestPage must get back.
        RequestPage.Rows.SetFilter("Entry No.", '1');
        RequestPage.OK().Invoke();
    end;

    [RequestPageHandler]
    procedure CancellingRequestPageHandler(var RequestPage: TestRequestPage "RRP Request Page Report")
    var
        LogRec: Record "RRP Log";
    begin
        LogRec.Log('rp-cancel');
        RequestPage.Cancel().Invoke();
    end;
}
