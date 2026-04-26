/// A table holding a Blob, used internally by BSC TempBlobLike codeunit.
table 163100 "BSC TempBlobData"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Content; Blob) { }
    }
}

/// A codeunit that exposes CreateOutStream() returning OutStream and
/// CreateInStream() returning InStream — mimicking the pattern of
/// Codeunit "Temp Blob" from BC standard library (issue #1026).
codeunit 163101 "BSC TempBlobLike"
{
    var
        Rec: Record "BSC TempBlobData";

    /// Returns an OutStream backed by the internal Blob. Caller can chain .Write*() on it.
    procedure CreateOutStream(): OutStream
    var
        OStr: OutStream;
    begin
        Rec.Content.CreateOutStream(OStr);
        exit(OStr);
    end;

    /// Returns an InStream backed by the internal Blob.
    procedure CreateInStream(): InStream
    var
        IStr: InStream;
    begin
        Rec.Content.CreateInStream(IStr);
        exit(IStr);
    end;

    procedure HasValue(): Boolean
    begin
        exit(Rec.Content.HasValue());
    end;
}

/// Exercises the chained-call pattern: TempBlobLike.CreateOutStream().WriteText(...)
codeunit 163102 "BSC ChainedStreamSrc"
{
    procedure WriteTextChained(var TempBlob: Codeunit "BSC TempBlobLike"; InputText: Text)
    begin
        TempBlob.CreateOutStream().WriteText(InputText);
    end;

    procedure ReadTextChained(var TempBlob: Codeunit "BSC TempBlobLike"): Text
    var
        Result: Text;
    begin
        TempBlob.CreateInStream().ReadText(Result);
        exit(Result);
    end;
}
