/// Helper codeunit for OutStream.Write/InStream.Read typed overload tests (issue #1400).
/// Covers the 2-arg (value, length) forms and typed value overloads.
codeunit 1317000 "Stream Typed RW Src"
{
    // ── OutStream.Write (Integer) + InStream.Read (Integer) ──────────────────

    procedure WriteInt_ReadInt(v: Integer): Integer
    var
        Rec: Record "STRW Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Integer;
    begin
        Rec.Init();
        Rec.Data.CreateOutStream(OutStr);
        OutStr.Write(v);
        Rec.Data.CreateInStream(InStr);
        InStr.Read(Result);
        exit(Result);
    end;

    // ── OutStream.Write (Boolean) + InStream.Read (Boolean) ──────────────────

    procedure WriteBool_ReadBool(b: Boolean): Boolean
    var
        Rec: Record "STRW Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Boolean;
    begin
        Rec.Init();
        Rec.Data.CreateOutStream(OutStr);
        OutStr.Write(b);
        Rec.Data.CreateInStream(InStr);
        InStr.Read(Result);
        exit(Result);
    end;

    // ── OutStream.Write (Decimal) + InStream.Read (Decimal) ──────────────────

    procedure WriteDecimal_ReadDecimal(d: Decimal): Decimal
    var
        Rec: Record "STRW Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Decimal;
    begin
        Rec.Init();
        Rec.Data.CreateOutStream(OutStr);
        OutStr.Write(d);
        Rec.Data.CreateInStream(InStr);
        InStr.Read(Result);
        exit(Result);
    end;

    // ── OutStream.Write (Integer, Length) — 2-arg form ───────────────────────
    // The second arg restricts the number of bytes written.

    procedure WriteInt_WithLength(v: Integer; len: Integer): Integer
    var
        Rec: Record "STRW Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Integer;
    begin
        Rec.Init();
        Rec.Data.CreateOutStream(OutStr);
        OutStr.Write(v, len);
        Rec.Data.CreateInStream(InStr);
        InStr.Read(Result);
        exit(Result);
    end;

    // ── OutStream.Write (Boolean, Length) — 2-arg form ───────────────────────

    procedure WriteBool_WithLength(b: Boolean; len: Integer): Boolean
    var
        Rec: Record "STRW Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Boolean;
    begin
        Rec.Init();
        Rec.Data.CreateOutStream(OutStr);
        OutStr.Write(b, len);
        Rec.Data.CreateInStream(InStr);
        InStr.Read(Result);
        exit(Result);
    end;

    // ── InStream.Read (Integer, MaxLength) — 2-arg form ──────────────────────

    procedure ReadInt_WithLength(v: Integer; maxLen: Integer): Integer
    var
        Rec: Record "STRW Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Integer;
    begin
        Rec.Init();
        Rec.Data.CreateOutStream(OutStr);
        OutStr.Write(v);
        Rec.Data.CreateInStream(InStr);
        InStr.Read(Result, maxLen);
        exit(Result);
    end;

    // ── InStream.Read (Boolean, MaxLength) — 2-arg form ─────────────────────

    procedure ReadBool_WithLength(b: Boolean; maxLen: Integer): Boolean
    var
        Rec: Record "STRW Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Boolean;
    begin
        Rec.Init();
        Rec.Data.CreateOutStream(OutStr);
        OutStr.Write(b);
        Rec.Data.CreateInStream(InStr);
        InStr.Read(Result, maxLen);
        exit(Result);
    end;

    // ── InStream.Read (Decimal, MaxLength) — 2-arg form ─────────────────────

    procedure ReadDecimal_WithLength(d: Decimal; maxLen: Integer): Decimal
    var
        Rec: Record "STRW Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Decimal;
    begin
        Rec.Init();
        Rec.Data.CreateOutStream(OutStr);
        OutStr.Write(d);
        Rec.Data.CreateInStream(InStr);
        InStr.Read(Result, maxLen);
        exit(Result);
    end;
}

table 1317000 "STRW Blob"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Data; Blob) { }
    }
    keys { key(PK; PK) { } }
}
