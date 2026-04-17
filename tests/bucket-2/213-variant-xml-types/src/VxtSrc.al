/// Helper codeunit exercising Variant.IsXxx for XML types and enum-like stubs
/// (issue #987). Covers the methods that were previously untested stubs.
codeunit 60392 "VXT Src"
{
    // ── XmlDocumentType ───────────────────────────────────────────────────────

    procedure IsXmlDocumentType_ForDocType(): Boolean
    var
        v: Variant;
        DocType: XmlDocumentType;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        v := DocType;
        exit(v.IsXmlDocumentType());
    end;

    procedure IsXmlDocumentType_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlDocumentType());
    end;

    // ── XmlAttributeCollection ────────────────────────────────────────────────

    procedure IsXmlAttributeCollection_ForAttrCollection(): Boolean
    var
        v: Variant;
        Elem: XmlElement;
        AttrCol: XmlAttributeCollection;
    begin
        Elem := XmlElement.Create('root');
        Elem.SetAttribute('x', '1');
        Elem.Attributes(AttrCol);
        v := AttrCol;
        exit(v.IsXmlAttributeCollection());
    end;

    procedure IsXmlAttributeCollection_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlAttributeCollection());
    end;

    // ── XmlNamespaceManager ───────────────────────────────────────────────────

    procedure IsXmlNamespaceManager_ForNsMgr(): Boolean
    var
        v: Variant;
        NameTable: XmlNameTable;
        NsMgr: XmlNamespaceManager;
    begin
        NsMgr := XmlNamespaceManager.Create(NameTable);
        v := NsMgr;
        exit(v.IsXmlNamespaceManager());
    end;

    procedure IsXmlNamespaceManager_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlNamespaceManager());
    end;

    // ── XmlNameTable ──────────────────────────────────────────────────────────

    procedure IsXmlNameTable_ForNameTable(): Boolean
    var
        v: Variant;
        NameTable: XmlNameTable;
    begin
        v := NameTable;
        exit(v.IsXmlNameTable());
    end;

    procedure IsXmlNameTable_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlNameTable());
    end;

    // ── XmlReadOptions ────────────────────────────────────────────────────────

    procedure IsXmlReadOptions_ForReadOptions(): Boolean
    var
        v: Variant;
        Opts: XmlReadOptions;
    begin
        v := Opts;
        exit(v.IsXmlReadOptions());
    end;

    procedure IsXmlReadOptions_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlReadOptions());
    end;

    // ── XmlWriteOptions ───────────────────────────────────────────────────────

    procedure IsXmlWriteOptions_ForWriteOptions(): Boolean
    var
        v: Variant;
        Opts: XmlWriteOptions;
    begin
        v := Opts;
        exit(v.IsXmlWriteOptions());
    end;

    procedure IsXmlWriteOptions_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlWriteOptions());
    end;

    // ── FilterPageBuilder ─────────────────────────────────────────────────────

    procedure IsFilterPageBuilder_ForFPB(): Boolean
    var
        v: Variant;
        FPB: FilterPageBuilder;
    begin
        v := FPB;
        exit(v.IsFilterPageBuilder());
    end;

    procedure IsFilterPageBuilder_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsFilterPageBuilder());
    end;

    // ── ReportFormat (always false — enum not detectable in Variant) ───────────

    procedure IsReportFormat_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsReportFormat());
    end;

    // ── PromptMode (always false — enum not detectable in Variant) ────────────

    procedure IsPromptMode_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsPromptMode());
    end;
}
