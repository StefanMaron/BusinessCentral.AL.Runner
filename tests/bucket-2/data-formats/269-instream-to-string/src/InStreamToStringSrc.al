/// Helper codeunit: MockInStream implicit conversion to string — issue #1273.
///
/// BC compiler sometimes emits NavInStream (rewritten to MockInStream) where
/// a string parameter is expected. Without an implicit operator string on
/// MockInStream, this causes CS1503 at Roslyn compilation.
///
/// WriteAndReadBlob exercises InStream read-back to prove the implicit
/// conversion works end-to-end.
///
/// GetVersion() is a pure-logic sentinel to confirm the codeunit compiled.
table 304010 "InStr To Str Table"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Data; Blob) { }
    }
}

codeunit 304010 "InStream To String Src"
{
    /// Writes text into a Blob via OutStream, reads it back via InStream.ReadText.
    procedure WriteAndReadBlob(Input: Text): Text
    var
        Rec: Record "InStr To Str Table";
        OStr: OutStream;
        IStr: InStream;
        Result: Text;
    begin
        Rec.Data.CreateOutStream(OStr);
        OStr.WriteText(Input);
        Rec.Data.CreateInStream(IStr);
        IStr.ReadText(Result);
        exit(Result);
    end;

    /// Pure-logic sentinel: confirms the codeunit compiled.
    procedure GetVersion(): Integer
    begin
        exit(1273);
    end;
}
