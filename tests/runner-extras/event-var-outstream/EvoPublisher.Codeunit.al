/// <summary>
/// Publisher half of the by-var event contract, shaped exactly like BC's
/// Codeunit ReportManagement :: OnCustomDocumentMergerEx — a `var OutStream`
/// the subscriber writes the produced document into, and a `var Boolean` it
/// flips to claim the work.
/// </summary>
codeunit 61900 "EVO Publisher"
{
    [IntegrationEvent(false, false)]
    local procedure OnProbeMerge(PayloadTag: Text; var DocumentStream: OutStream; var IsHandled: Boolean)
    begin
    end;

    /// <summary>
    /// Raises the event and reports back what the subscriber managed to change.
    /// The OutStream belongs to the CALLER's Temp Blob, so a subscriber write is
    /// only observable here if the by-var parameter really is the caller's stream.
    /// </summary>
    procedure Publish(PayloadTag: Text; var DocumentStream: OutStream) Handled: Boolean
    begin
        OnProbeMerge(PayloadTag, DocumentStream, Handled);
    end;
}
