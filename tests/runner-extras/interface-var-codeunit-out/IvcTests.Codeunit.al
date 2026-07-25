/// <summary>
/// Pins that a `var Result: Codeunit "Temp Blob"` out-parameter filled by an
/// interface implementation is visible in the CALLER's variable.
///
/// Why: Report.SaveAs(..., Pdf, ...) against an ISV renderer writes zero bytes.
/// Tracing the whole chain showed the AL merger subscriber DOES run and DOES
/// reach its final `CopyStream(DocumentStream, ResultInStream)` — but with a
/// source of length 0, i.e. the Temp Blob that
/// `Backend.Render(RenderContext, TempBlobResult)` was supposed to fill came
/// back empty, with nothing raised. Everything downstream of that point
/// (layout mimetype, layout bytes, document-stream propagation) was
/// investigated and is NOT the cause.
///
/// The direct-call test is the control: if it passes and the interface one
/// fails, the defect is in interface dispatch specifically, not in by-var
/// codeunit parameters generally.
/// </summary>
codeunit 61913 "IVC Tests"
{
    Subtype = Test;

    [Test]
    procedure DirectCall_FillsTheCallersTempBlob()
    var
        Impl: Codeunit "IVC Native Impl";
        Blob: Codeunit "Temp Blob";
        Reader: Codeunit "IVC Reader";
    begin
        // Control: same by-var codeunit contract, no interface in the way.
        Impl.Produce(Blob, 'PRODUCED-DIRECT');

        if Reader.ReadAll(Blob) <> 'PRODUCED-DIRECT' then
            Error('Direct call: caller''s Temp Blob is "%1", expected "PRODUCED-DIRECT" — a by-var Codeunit out-parameter did not travel back.', Reader.ReadAll(Blob));
    end;

    [Test]
    procedure InterfaceDispatch_FillsTheCallersTempBlob()
    var
        Backend: Interface "IVC Backend";
        Blob: Codeunit "Temp Blob";
        Reader: Codeunit "IVC Reader";
    begin
        // The exact ISV renderer shape: enum value assigned to an interface,
        // result returned only through the by-var codeunit.
        Backend := Enum::"IVC Backend Type"::NativeProduce;
        Backend.Produce(Blob, 'PRODUCED-VIA-INTERFACE');

        if Reader.ReadAll(Blob) <> 'PRODUCED-VIA-INTERFACE' then
            Error('Interface dispatch: caller''s Temp Blob is "%1", expected "PRODUCED-VIA-INTERFACE" — the implementation filled a COPY, so its output never reached the caller.', Reader.ReadAll(Blob));
    end;

    [Test]
    procedure SecondCreateInStream_StillSeesTheContent()
    var
        Impl: Codeunit "IVC Native Impl";
        Blob: Codeunit "Temp Blob";
        Reader: Codeunit "IVC Reader";
        First: Text;
        Second: Text;
    begin
        // The real subscriber reads its result blob TWICE: once to count pages,
        // then again to copy it out. If the runner's Temp Blob is exhausted by the
        // first full read, the second CreateInStream yields nothing and the
        // document is silently lost on the way out.
        Impl.Produce(Blob, 'PRODUCED-TWICE');

        First := Reader.ReadAll(Blob);
        Second := Reader.ReadAll(Blob);

        if First <> 'PRODUCED-TWICE' then
            Error('First read returned "%1", expected "PRODUCED-TWICE".', First);
        if Second <> 'PRODUCED-TWICE' then
            Error('Second CreateInStream returned "%1" — the Temp Blob was consumed by the first read, so a caller that inspects before copying loses the content.', Second);
    end;

    [Test]
    procedure ClearOnAVarCodeunitParameter_DoesNotDetachTheCallersInstance()
    var
        Impl: Codeunit "IVC Clearing Impl";
        Blob: Codeunit "Temp Blob";
        Reader: Codeunit "IVC Reader";
        Actual: Text;
    begin
        // Real BC: Clear(Result) resets the instance IN PLACE, so the caller keeps
        // observing the same object and sees everything written afterwards. If the
        // runner instead rebinds the local to a FRESH instance, every subsequent
        // write lands somewhere the caller cannot see — and nothing is raised.
        // This is the exact prologue of the ISV PDF writer's Finish():
        //     Clear(ResultBlob); ResultBlob.CreateOutStream(...); Write...
        Impl.ProduceAfterClear(Blob, 'PRODUCED-AFTER-CLEAR');

        Actual := Reader.ReadAll(Blob);
        if Actual <> 'PRODUCED-AFTER-CLEAR' then
            Error('After Clear() on a var Codeunit parameter the caller sees "%1", expected "PRODUCED-AFTER-CLEAR" — Clear detached the parameter from the caller''s instance, so the produced bytes are lost silently.', Actual);
    end;

    [Test]
    procedure CreateOutStreamWithTextEncoding_StillReachesTheCaller()
    var
        Impl: Codeunit "IVC Clearing Impl";
        Blob: Codeunit "Temp Blob";
        Reader: Codeunit "IVC Reader";
        Actual: Text;
    begin
        // The writer opens its stream as CreateOutStream(Stream, TextEncoding::Windows).
        // Pinned separately so an unimplemented encoding overload cannot hide behind
        // the plain one.
        Impl.ProduceWithEncoding(Blob, 'PRODUCED-WINDOWS-ENCODED');

        Actual := Reader.ReadAll(Blob);
        if Actual <> 'PRODUCED-WINDOWS-ENCODED' then
            Error('CreateOutStream(.., TextEncoding::Windows) on a var Codeunit parameter yielded "%1", expected "PRODUCED-WINDOWS-ENCODED".', Actual);
    end;

    [Test]
    procedure UntouchedTempBlob_IsEmpty()
    var
        Blob: Codeunit "Temp Blob";
        Reader: Codeunit "IVC Reader";
    begin
        // Negative control: proves ReadAll reports emptiness rather than always
        // finding the expected text, so the two assertions above mean something.
        if Reader.ReadAll(Blob) <> '' then
            Error('A Temp Blob nobody wrote to reported content: "%1"', Reader.ReadAll(Blob));
    end;
}
