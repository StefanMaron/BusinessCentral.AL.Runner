/// <summary>
/// Subscriber half. Mirrors the real Pageworks renderer's final two statements:
/// it builds its output in its OWN Temp Blob, copies that into the by-var
/// OutStream it was handed, and only then claims the work with IsHandled.
///
/// The PayloadTag gate mirrors the real subscriber's `if not IsPageworksLayout(...) then exit`,
/// so the suite can exercise both the engaged and the declined path.
/// </summary>
codeunit 61901 "EVO Subscriber"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"EVO Publisher", 'OnProbeMerge', '', true, true)]
    local procedure OnProbeMerge(PayloadTag: Text; var DocumentStream: OutStream; var IsHandled: Boolean)
    var
        TempBlobResult: Codeunit "Temp Blob";
        ResultOutStream: OutStream;
        ResultInStream: InStream;
    begin
        if IsHandled then
            exit;
        if PayloadTag <> 'render' then
            exit;

        TempBlobResult.CreateOutStream(ResultOutStream);
        ResultOutStream.WriteText('RENDERED-BY-SUBSCRIBER');

        TempBlobResult.CreateInStream(ResultInStream);
        CopyStream(DocumentStream, ResultInStream);
        IsHandled := true;
    end;
}
