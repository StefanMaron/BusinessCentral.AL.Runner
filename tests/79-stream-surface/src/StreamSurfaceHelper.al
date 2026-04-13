codeunit 79100 StreamSurfaceHelper
{
    /// <summary>
    /// Writes text via an assigned OutStream (tests OutStr2 := OutStr1).
    /// </summary>
    procedure WriteViaAssignedOutStream(var Rec: Record StreamSurfaceTable; Text: Text): Text
    var
        OStr1: OutStream;
        OStr2: OutStream;
        IStr: InStream;
        Result: Text;
    begin
        Rec.Data.CreateOutStream(OStr1);
        OStr2 := OStr1;
        OStr2.WriteText(Text);

        Rec.Data.CreateInStream(IStr);
        IStr.ReadText(Result);
        exit(Result);
    end;

    /// <summary>
    /// Reads text via an assigned InStream (tests InStr2 := InStr1).
    /// </summary>
    procedure ReadViaAssignedInStream(var Rec: Record StreamSurfaceTable): Text
    var
        IStr1: InStream;
        IStr2: InStream;
        Result: Text;
    begin
        Rec.Data.CreateInStream(IStr1);
        IStr2 := IStr1;
        IStr2.ReadText(Result);
        exit(Result);
    end;

    /// <summary>
    /// Copies a BLOB from SrcRec to DstRec using COPYSTREAM.
    /// </summary>
    procedure CopyBlobData(var SrcRec: Record StreamSurfaceTable; var DstRec: Record StreamSurfaceTable)
    var
        IStr: InStream;
        OStr: OutStream;
    begin
        SrcRec.Data.CreateInStream(IStr);
        DstRec.Data.CreateOutStream(OStr);
        COPYSTREAM(OStr, IStr);
    end;

    /// <summary>
    /// Writes and reads back an Integer via binary stream (tests Write/Read Integer).
    /// </summary>
    procedure WriteReadInteger(var Rec: Record StreamSurfaceTable; Value: Integer): Integer
    var
        OStr: OutStream;
        IStr: InStream;
        ReadVal: Integer;
        BytesRead: Integer;
    begin
        Rec.Data.CreateOutStream(OStr);
        OStr.Write(Value);

        Rec.Data.CreateInStream(IStr);
        BytesRead := IStr.Read(ReadVal);
        exit(ReadVal);
    end;

    /// <summary>
    /// Writes and reads back a Boolean via binary stream (tests Write/Read Boolean).
    /// </summary>
    procedure WriteReadBoolean(var Rec: Record StreamSurfaceTable; Value: Boolean): Boolean
    var
        OStr: OutStream;
        IStr: InStream;
        ReadVal: Boolean;
        BytesRead: Integer;
    begin
        Rec.Data.CreateOutStream(OStr);
        OStr.Write(Value);

        Rec.Data.CreateInStream(IStr);
        BytesRead := IStr.Read(ReadVal);
        exit(ReadVal);
    end;

    /// <summary>
    /// Writes and reads back a Decimal via binary stream (tests Write/Read Decimal).
    /// </summary>
    procedure WriteReadDecimal(var Rec: Record StreamSurfaceTable; Value: Decimal): Decimal
    var
        OStr: OutStream;
        IStr: InStream;
        ReadVal: Decimal;
        BytesRead: Integer;
    begin
        Rec.Data.CreateOutStream(OStr);
        OStr.Write(Value);

        Rec.Data.CreateInStream(IStr);
        BytesRead := IStr.Read(ReadVal);
        exit(ReadVal);
    end;

    /// <summary>
    /// Passes OutStream/InStream as var parameters to a sub-procedure.
    /// </summary>
    procedure WriteReadViaParam(var Rec: Record StreamSurfaceTable; Text: Text): Text
    var
        OStr: OutStream;
        IStr: InStream;
        Result: Text;
    begin
        Rec.Data.CreateOutStream(OStr);
        WriteToStream(OStr, Text);

        Rec.Data.CreateInStream(IStr);
        ReadFromStream(IStr, Result);
        exit(Result);
    end;

    local procedure WriteToStream(var OStr: OutStream; Text: Text)
    begin
        OStr.WriteText(Text);
    end;

    local procedure ReadFromStream(var IStr: InStream; var Result: Text)
    begin
        IStr.ReadText(Result);
    end;

    /// <summary>
    /// Tests EOS detection.
    /// </summary>
    procedure IsEOSAfterRead(var Rec: Record StreamSurfaceTable): Boolean
    var
        IStr: InStream;
        Dummy: Text;
    begin
        Rec.Data.CreateInStream(IStr);
        IStr.ReadText(Dummy);
        exit(IStr.EOS());
    end;
}
