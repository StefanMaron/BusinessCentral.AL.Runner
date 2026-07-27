// Captures what the custom-document merger is actually handed.
//
// The rest of this suite proves the layout NAME picked the right layout — the render fork
// changes, the virtual-table rows are right. None of that proves the layout's CONTENT ever
// reaches the renderer, and it did not: for a report declaring MORE THAN ONE layout the
// runner skipped layout hydration entirely, so the merger received an EMPTY template while
// every by-name assertion still passed. The observable symptom was an AL-side
// "LF-XML: The template is not well-formed XML: 'Root element is missing.'" — nine
// Pageworks tests — which reads as a template bug rather than a runner one.
//
// This subscriber is the missing observable: it records the template bytes BC hands the
// merger, so a test can assert the SELECTED layout's own content arrived.
//
// It lives in its own non-test codeunit so it is bound automatically for the whole run,
// and it handles the render (IsHandled := true) exactly as a real ISV rendering extension
// would — that fork is the in-scope custom-merger path.
codeunit 61874 "RLB Merger Capture"
{
    procedure CapturedTemplate(): Text
    var
        Sample: Record "RLB Sample";
        InStr: InStream;
        Captured: Text;
    begin
        if not Sample.Get(CaptureEntryNo()) then
            exit('');
        Sample.CalcFields("Blob Data");
        Sample."Blob Data".CreateInStream(InStr);
        InStr.ReadText(Captured);
        exit(Captured);
    end;

    procedure ClearCapture()
    var
        Sample: Record "RLB Sample";
    begin
        if Sample.Get(CaptureEntryNo()) then
            Sample.Delete();
    end;

    local procedure CaptureEntryNo(): Integer
    begin
        exit(999);
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::ReportManagement, OnCustomDocumentMergerEx, '', true, true)]
    local procedure OnCustomDocumentMergerEx(ObjectID: Integer; ReportAction: Option SaveAsPdf,SaveAsWord,SaveAsExcel,Preview,Print,SaveAsHtml; ObjectPayload: JsonObject; var XmlData: InStream; LayoutData: InStream; var DocumentStream: OutStream; var IsHandled: Boolean)
    var
        Sample: Record "RLB Sample";
        OutStr: OutStream;
        TemplateText: Text;
        Line: Text;
    begin
        while not LayoutData.EOS() do begin
            LayoutData.ReadText(Line);
            TemplateText += Line;
        end;

        if Sample.Get(CaptureEntryNo()) then
            Sample.Delete();
        Sample.Init();
        Sample."Entry No." := CaptureEntryNo();
        Sample.Description := 'captured template';
        Sample."Blob Data".CreateOutStream(OutStr);
        OutStr.WriteText(TemplateText);
        Sample.Insert();

        // Stand in for the ISV renderer: produce *something* so the render completes and
        // the suite's existing fork assertions keep holding.
        DocumentStream.WriteText('RLB-RENDERED');
        IsHandled := true;
    end;
}
