/// Temporary table providing a Blob field for OutStream/InStream roundtrip.
table 309300 "XWT Blob"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Data; Blob) { }
    }
    keys { key(PK; PK) { } }
}

/// Helper codeunit exercising Xml*.WriteTo per-format overloads (issue #1370).
/// Covers: WriteTo(var Text), WriteTo(XmlWriteOptions, var OutStream), WriteTo(XmlWriteOptions, var Text)
/// on XmlDocument, XmlElement, XmlCData, XmlComment, XmlDeclaration, XmlText,
/// XmlAttribute, XmlProcessingInstruction, and XmlNode.
codeunit 309301 "XWT Src"
{
    // ── XmlDocument ───────────────────────────────────────────────────────────

    /// XmlDocument.WriteTo(var Text) — simple text-output overload.
    procedure XmlDocWriteToText(ElemName: Text): Text
    var
        Doc: XmlDocument;
        Elem: XmlElement;
        Result: Text;
    begin
        Doc := XmlDocument.Create();
        Elem := XmlElement.Create(ElemName);
        Doc.Add(Elem);
        Doc.WriteTo(Result);
        exit(Result);
    end;

    /// XmlDocument.WriteTo(XmlWriteOptions, var Text) — options + text overload.
    procedure XmlDocWriteToOptionsText(ElemName: Text): Text
    var
        Doc: XmlDocument;
        Elem: XmlElement;
        Opts: XmlWriteOptions;
        Result: Text;
    begin
        Doc := XmlDocument.Create();
        Elem := XmlElement.Create(ElemName);
        Doc.Add(Elem);
        Doc.WriteTo(Opts, Result);
        exit(Result);
    end;

    /// XmlDocument.WriteTo(XmlWriteOptions, var OutStream) — options + stream overload.
    procedure XmlDocWriteToOptionsStream(ElemName: Text): Text
    var
        Doc: XmlDocument;
        Elem: XmlElement;
        Opts: XmlWriteOptions;
        BlobRec: Record "XWT Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Text;
    begin
        Doc := XmlDocument.Create();
        Elem := XmlElement.Create(ElemName);
        Doc.Add(Elem);
        BlobRec.Data.CreateOutStream(OutStr);
        Doc.WriteTo(Opts, OutStr);
        BlobRec.Data.CreateInStream(InStr);
        InStr.ReadText(Result);
        exit(Result);
    end;

    // ── XmlElement ────────────────────────────────────────────────────────────

    /// XmlElement.WriteTo(var Text).
    procedure XmlElemWriteToText(ElemName: Text): Text
    var
        Elem: XmlElement;
        Result: Text;
    begin
        Elem := XmlElement.Create(ElemName);
        Elem.WriteTo(Result);
        exit(Result);
    end;

    /// XmlElement.WriteTo(XmlWriteOptions, var Text).
    procedure XmlElemWriteToOptionsText(ElemName: Text): Text
    var
        Elem: XmlElement;
        Opts: XmlWriteOptions;
        Result: Text;
    begin
        Elem := XmlElement.Create(ElemName);
        Elem.WriteTo(Opts, Result);
        exit(Result);
    end;

    /// XmlElement.WriteTo(XmlWriteOptions, var OutStream).
    procedure XmlElemWriteToOptionsStream(ElemName: Text): Text
    var
        Elem: XmlElement;
        Opts: XmlWriteOptions;
        BlobRec: Record "XWT Blob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Text;
    begin
        Elem := XmlElement.Create(ElemName);
        BlobRec.Data.CreateOutStream(OutStr);
        Elem.WriteTo(Opts, OutStr);
        BlobRec.Data.CreateInStream(InStr);
        InStr.ReadText(Result);
        exit(Result);
    end;

    // ── XmlCData ──────────────────────────────────────────────────────────────

    /// XmlCData.WriteTo(var Text).
    procedure XmlCDataWriteToText(Value: Text): Text
    var
        CData: XmlCData;
        Result: Text;
    begin
        CData := XmlCData.Create(Value);
        CData.WriteTo(Result);
        exit(Result);
    end;

    /// XmlCData.WriteTo(XmlWriteOptions, var Text).
    procedure XmlCDataWriteToOptionsText(Value: Text): Text
    var
        CData: XmlCData;
        Opts: XmlWriteOptions;
        Result: Text;
    begin
        CData := XmlCData.Create(Value);
        CData.WriteTo(Opts, Result);
        exit(Result);
    end;

    // ── XmlComment ────────────────────────────────────────────────────────────

    /// XmlComment.WriteTo(var Text).
    procedure XmlCommentWriteToText(Value: Text): Text
    var
        Comment: XmlComment;
        Result: Text;
    begin
        Comment := XmlComment.Create(Value);
        Comment.WriteTo(Result);
        exit(Result);
    end;

    /// XmlComment.WriteTo(XmlWriteOptions, var Text).
    procedure XmlCommentWriteToOptionsText(Value: Text): Text
    var
        Comment: XmlComment;
        Opts: XmlWriteOptions;
        Result: Text;
    begin
        Comment := XmlComment.Create(Value);
        Comment.WriteTo(Opts, Result);
        exit(Result);
    end;

    // ── XmlDeclaration ────────────────────────────────────────────────────────

    /// XmlDeclaration.WriteTo(var Text).
    procedure XmlDeclWriteToText(): Text
    var
        Decl: XmlDeclaration;
        Result: Text;
    begin
        Decl := XmlDeclaration.Create('1.0', 'UTF-8', '');
        Decl.WriteTo(Result);
        exit(Result);
    end;

    /// XmlDeclaration.WriteTo(XmlWriteOptions, var Text).
    procedure XmlDeclWriteToOptionsText(): Text
    var
        Decl: XmlDeclaration;
        Opts: XmlWriteOptions;
        Result: Text;
    begin
        Decl := XmlDeclaration.Create('1.0', 'UTF-8', '');
        Decl.WriteTo(Opts, Result);
        exit(Result);
    end;

    // ── XmlText ───────────────────────────────────────────────────────────────

    /// XmlText.WriteTo(var Text).
    procedure XmlTextWriteToText(Value: Text): Text
    var
        XText: XmlText;
        Result: Text;
    begin
        XText := XmlText.Create(Value);
        XText.WriteTo(Result);
        exit(Result);
    end;

    /// XmlText.WriteTo(XmlWriteOptions, var Text).
    procedure XmlTextWriteToOptionsText(Value: Text): Text
    var
        XText: XmlText;
        Opts: XmlWriteOptions;
        Result: Text;
    begin
        XText := XmlText.Create(Value);
        XText.WriteTo(Opts, Result);
        exit(Result);
    end;

    // ── XmlAttribute ──────────────────────────────────────────────────────────

    /// XmlAttribute.WriteTo(var Text).
    procedure XmlAttrWriteToText(AttrName: Text; AttrValue: Text): Text
    var
        Attr: XmlAttribute;
        Result: Text;
    begin
        Attr := XmlAttribute.Create(AttrName, AttrValue);
        Attr.WriteTo(Result);
        exit(Result);
    end;

    /// XmlAttribute.WriteTo(XmlWriteOptions, var Text).
    procedure XmlAttrWriteToOptionsText(AttrName: Text; AttrValue: Text): Text
    var
        Attr: XmlAttribute;
        Opts: XmlWriteOptions;
        Result: Text;
    begin
        Attr := XmlAttribute.Create(AttrName, AttrValue);
        Attr.WriteTo(Opts, Result);
        exit(Result);
    end;

    // ── XmlProcessingInstruction ──────────────────────────────────────────────

    /// XmlProcessingInstruction.WriteTo(var Text).
    procedure XmlPIWriteToText(Target: Text; Data: Text): Text
    var
        PI: XmlProcessingInstruction;
        Result: Text;
    begin
        PI := XmlProcessingInstruction.Create(Target, Data);
        PI.WriteTo(Result);
        exit(Result);
    end;

    /// XmlProcessingInstruction.WriteTo(XmlWriteOptions, var Text).
    procedure XmlPIWriteToOptionsText(Target: Text; Data: Text): Text
    var
        PI: XmlProcessingInstruction;
        Opts: XmlWriteOptions;
        Result: Text;
    begin
        PI := XmlProcessingInstruction.Create(Target, Data);
        PI.WriteTo(Opts, Result);
        exit(Result);
    end;

    // ── XmlNode ───────────────────────────────────────────────────────────────

    /// XmlNode.WriteTo(var Text) — via element cast to XmlNode.
    procedure XmlNodeWriteToText(ElemName: Text): Text
    var
        Elem: XmlElement;
        Node: XmlNode;
        Result: Text;
    begin
        Elem := XmlElement.Create(ElemName);
        Node := Elem.AsXmlNode();
        Node.WriteTo(Result);
        exit(Result);
    end;

    /// XmlNode.WriteTo(XmlWriteOptions, var Text).
    procedure XmlNodeWriteToOptionsText(ElemName: Text): Text
    var
        Elem: XmlElement;
        Node: XmlNode;
        Opts: XmlWriteOptions;
        Result: Text;
    begin
        Elem := XmlElement.Create(ElemName);
        Node := Elem.AsXmlNode();
        Node.WriteTo(Opts, Result);
        exit(Result);
    end;
}
