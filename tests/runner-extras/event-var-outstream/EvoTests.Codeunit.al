/// <summary>
/// Pins the by-var event contract that BC's report custom-merger depends on.
///
/// Why this exists: Report.SaveAs(..., Pdf, ...) against an ISV renderer was
/// observed writing ZERO bytes while the subscriber demonstrably executed
/// (measured: ~0.9s of extra render work). The subscriber's last two statements
/// are `CopyStream(DocumentStream, ResultInStream); IsHandled := true` against
/// by-var parameters — so either the runner delivers those writes to the
/// publisher, or the whole merger contract silently produces nothing. These
/// tests decide that question without any report, layout or BC rendering
/// machinery in the way.
/// </summary>
codeunit 61902 "EVO Tests"
{
    Subtype = Test;

    [Test]
    procedure SubscriberWriteToVarOutStream_ReachesThePublishersCaller()
    var
        Publisher: Codeunit "EVO Publisher";
        TempBlob: Codeunit "Temp Blob";
        DocumentStream: OutStream;
        ResultInStream: InStream;
        Written: Text;
        Handled: Boolean;
    begin
        TempBlob.CreateOutStream(DocumentStream);

        Handled := Publisher.Publish('render', DocumentStream);

        if not Handled then
            Error('The subscriber set IsHandled := true, but the publisher observed false — the by-var Boolean did not travel back across event dispatch.');

        TempBlob.CreateInStream(ResultInStream);
        ResultInStream.ReadText(Written);

        if Written = '' then
            Error('The subscriber wrote to the by-var OutStream, but the caller''s Temp Blob is EMPTY — the subscriber was handed a stream that is not the caller''s.');
        if Written <> 'RENDERED-BY-SUBSCRIBER' then
            Error('Expected the subscriber''s exact bytes back, got: %1', Written);
    end;

    [Test]
    procedure DeclinedSubscriber_LeavesStreamEmptyAndUnhandled()
    var
        Publisher: Codeunit "EVO Publisher";
        TempBlob: Codeunit "Temp Blob";
        DocumentStream: OutStream;
        ResultInStream: InStream;
        Written: Text;
        Handled: Boolean;
    begin
        // Negative control: without this, a dispatch that wrote the expected bytes
        // unconditionally — or a test asserting against a pre-filled blob — would
        // pass the positive case for the wrong reason.
        TempBlob.CreateOutStream(DocumentStream);

        Handled := Publisher.Publish('not-mine', DocumentStream);

        if Handled then
            Error('The subscriber declined this payload, yet the publisher observed IsHandled = true.');

        TempBlob.CreateInStream(ResultInStream);
        ResultInStream.ReadText(Written);
        if Written <> '' then
            Error('The subscriber declined this payload, yet bytes appeared in the caller''s stream: %1', Written);
    end;
}
