/// Helper codeunit + table exercising Variant.IsXxx type-check methods (issue #844).
/// Tests cover the subset where the runner has mock support:
///   XmlDocument, XmlElement, XmlNode, XmlProcessingInstruction, XmlCData,
///   XmlComment, XmlDeclaration, XmlText, XmlNodeList, XmlAttribute,
///   InStream, OutStream, Codeunit, Dictionary.
table 118000 "VTC Data"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Content; Blob) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

codeunit 118001 "VTC Src"
{
    // ── XmlDocument ────────────────────────────────────────────────────────────

    procedure IsXmlDocument_ForDoc(): Boolean
    var
        v: Variant;
        Doc: XmlDocument;
    begin
        Doc := XmlDocument.Create();
        v := Doc;
        exit(v.IsXmlDocument());
    end;

    procedure IsXmlDocument_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlDocument());
    end;

    // ── XmlElement ────────────────────────────────────────────────────────────

    procedure IsXmlElement_ForElement(): Boolean
    var
        v: Variant;
        Elem: XmlElement;
    begin
        Elem := XmlElement.Create('root');
        v := Elem;
        exit(v.IsXmlElement());
    end;

    procedure IsXmlElement_ForDoc(): Boolean
    var
        v: Variant;
        Doc: XmlDocument;
    begin
        Doc := XmlDocument.Create();
        v := Doc;
        exit(v.IsXmlElement());
    end;

    // ── XmlNode ────────────────────────────────────────────────────────────────

    procedure IsXmlNode_ForElement(): Boolean
    var
        v: Variant;
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Elem := XmlElement.Create('root');
        Node := Elem.AsXmlNode();
        v := Node;
        exit(v.IsXmlNode());
    end;

    procedure IsXmlNode_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 99;
        exit(v.IsXmlNode());
    end;

    // ── XmlProcessingInstruction ──────────────────────────────────────────────

    procedure IsXmlPI_ForPI(): Boolean
    var
        v: Variant;
        PI: XmlProcessingInstruction;
    begin
        PI := XmlProcessingInstruction.Create('target', 'data');
        v := PI;
        exit(v.IsXmlProcessingInstruction());
    end;

    procedure IsXmlPI_ForElement(): Boolean
    var
        v: Variant;
        Elem: XmlElement;
    begin
        Elem := XmlElement.Create('root');
        v := Elem;
        exit(v.IsXmlProcessingInstruction());
    end;

    // ── XmlCData ──────────────────────────────────────────────────────────────

    procedure IsXmlCData_ForCData(): Boolean
    var
        v: Variant;
        CData: XmlCData;
    begin
        CData := XmlCData.Create('some data');
        v := CData;
        exit(v.IsXmlCData());
    end;

    procedure IsXmlCData_ForText(): Boolean
    var
        v: Variant;
    begin
        v := 'text';
        exit(v.IsXmlCData());
    end;

    // ── XmlComment ────────────────────────────────────────────────────────────

    procedure IsXmlComment_ForComment(): Boolean
    var
        v: Variant;
        Comment: XmlComment;
    begin
        Comment := XmlComment.Create('a comment');
        v := Comment;
        exit(v.IsXmlComment());
    end;

    procedure IsXmlComment_ForText(): Boolean
    var
        v: Variant;
    begin
        v := 'text';
        exit(v.IsXmlComment());
    end;

    // ── XmlDeclaration ────────────────────────────────────────────────────────

    procedure IsXmlDeclaration_ForDecl(): Boolean
    var
        v: Variant;
        Decl: XmlDeclaration;
    begin
        Decl := XmlDeclaration.Create('1.0', 'UTF-8', '');
        v := Decl;
        exit(v.IsXmlDeclaration());
    end;

    procedure IsXmlDeclaration_ForText(): Boolean
    var
        v: Variant;
    begin
        v := 'text';
        exit(v.IsXmlDeclaration());
    end;

    // ── XmlText ───────────────────────────────────────────────────────────────

    procedure IsXmlText_ForXmlText(): Boolean
    var
        v: Variant;
        XT: XmlText;
    begin
        XT := XmlText.Create('hello');
        v := XT;
        exit(v.IsXmlText());
    end;

    procedure IsXmlText_ForText(): Boolean
    var
        v: Variant;
    begin
        v := 'hello';
        exit(v.IsXmlText());
    end;

    // ── XmlNodeList ───────────────────────────────────────────────────────────

    procedure IsXmlNodeList_ForNodeList(): Boolean
    var
        v: Variant;
        Elem: XmlElement;
        NL: XmlNodeList;
    begin
        Elem := XmlElement.Create('root');
        NL := Elem.GetChildNodes();
        v := NL;
        exit(v.IsXmlNodeList());
    end;

    procedure IsXmlNodeList_ForText(): Boolean
    var
        v: Variant;
    begin
        v := 'text';
        exit(v.IsXmlNodeList());
    end;

    // ── XmlAttribute ──────────────────────────────────────────────────────────

    procedure IsXmlAttribute_ForAttr(): Boolean
    var
        v: Variant;
        Attr: XmlAttribute;
    begin
        Attr := XmlAttribute.Create('name', 'value');
        v := Attr;
        exit(v.IsXmlAttribute());
    end;

    procedure IsXmlAttribute_ForText(): Boolean
    var
        v: Variant;
    begin
        v := 'text';
        exit(v.IsXmlAttribute());
    end;

    // ── InStream / OutStream ──────────────────────────────────────────────────

    procedure IsInStream_ForInStream(): Boolean
    var
        v: Variant;
        Rec: Record "VTC Data";
        IS: InStream;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateInStream(IS);
        v := IS;
        exit(v.IsInStream());
    end;

    procedure IsInStream_ForText(): Boolean
    var
        v: Variant;
    begin
        v := 'hello';
        exit(v.IsInStream());
    end;

    procedure IsOutStream_ForOutStream(): Boolean
    var
        v: Variant;
        Rec: Record "VTC Data";
        OS: OutStream;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OS);
        v := OS;
        exit(v.IsOutStream());
    end;

    procedure IsOutStream_ForText(): Boolean
    var
        v: Variant;
    begin
        v := 'hello';
        exit(v.IsOutStream());
    end;

    // ── IsDictionary ──────────────────────────────────────────────────────────

    procedure IsDictionary_ForDict(): Boolean
    var
        v: Variant;
        D: Dictionary of [Text, Text];
    begin
        D.Add('k', 'v');
        v := D;
        exit(v.IsDictionary());
    end;

    procedure IsDictionary_ForText(): Boolean
    var
        v: Variant;
    begin
        v := 'text';
        exit(v.IsDictionary());
    end;
}
