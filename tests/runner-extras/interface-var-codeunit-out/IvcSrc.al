/// <summary>
/// Mirrors the ISV renderer shape BC's custom document merger drives:
/// an interface whose only output travels through a by-var Codeunit parameter,
/// implemented by an enum value.
/// </summary>
interface "IVC Backend"
{
    /// <summary>Fills <paramref name="Result"/>. Returns nothing by value — the
    /// by-var codeunit IS the result channel.</summary>
    procedure Produce(var Result: Codeunit "Temp Blob"; Payload: Text)
}

codeunit 61911 "IVC Native Impl" implements "IVC Backend"
{
    procedure Produce(var Result: Codeunit "Temp Blob"; Payload: Text)
    var
        ResultOutStream: OutStream;
    begin
        Result.CreateOutStream(ResultOutStream);
        ResultOutStream.WriteText(Payload);
    end;
}

enum 61910 "IVC Backend Type" implements "IVC Backend"
{
    Extensible = false;

    value(0; NativeProduce)
    {
        Implementation = "IVC Backend" = "IVC Native Impl";
    }
}

/// <summary>Helper so the test can read a Temp Blob back as text.</summary>
/// <summary>
/// Reproduces the ISV PDF writer's actual prologue: Clear() the by-var result
/// codeunit, then open its stream with an explicit TextEncoding. Both of those
/// sit between the caller's variable and the bytes.
/// </summary>
codeunit 61914 "IVC Clearing Impl"
{
    procedure ProduceAfterClear(var Result: Codeunit "Temp Blob"; Payload: Text)
    var
        ResultOutStream: OutStream;
    begin
        Clear(Result);
        Result.CreateOutStream(ResultOutStream);
        ResultOutStream.WriteText(Payload);
    end;

    procedure ProduceWithEncoding(var Result: Codeunit "Temp Blob"; Payload: Text)
    var
        ResultOutStream: OutStream;
    begin
        Clear(Result);
        Result.CreateOutStream(ResultOutStream, TextEncoding::Windows);
        ResultOutStream.WriteText(Payload);
    end;
}

codeunit 61912 "IVC Reader"
{
    procedure ReadAll(var Blob: Codeunit "Temp Blob") Contents: Text
    var
        BlobInStream: InStream;
        Line: Text;
    begin
        Blob.CreateInStream(BlobInStream);
        while not BlobInStream.EOS() do begin
            BlobInStream.ReadText(Line);
            Contents += Line;
        end;
    end;
}
