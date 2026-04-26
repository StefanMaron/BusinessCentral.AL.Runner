/// Helper codeunit exercising Variant.IsXxx for XML types and misc type stubs
/// (issue #987). Tests cover the false cases (non-matching Variant) plus the
/// true case for XmlDocumentType (an XmlNode subtype, so Variant-compatible).
/// Other types (XmlAttributeCollection, XmlNamespaceManager, XmlNameTable,
/// XmlReadOptions, XmlWriteOptions, FilterPageBuilder) are not directly
/// assignable to Variant in BC AL, so only the false case is provable.
codeunit 60392 "VXT Src"
{
    // ── XmlDocumentType ───────────────────────────────────────────────────────
    // XmlDocumentType is an XmlNode subtype — Variant-compatible.

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

    // ── XmlAttributeCollection (false-only) ────────────────────────────────────
    // XmlAttributeCollection is not directly Variant-assignable in BC AL.

    procedure IsXmlAttributeCollection_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlAttributeCollection());
    end;

    // ── XmlNamespaceManager (false-only) ──────────────────────────────────────
    // XmlNamespaceManager is not directly Variant-assignable in BC AL.

    procedure IsXmlNamespaceManager_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlNamespaceManager());
    end;

    // ── XmlNameTable (false-only) ─────────────────────────────────────────────
    // XmlNameTable is not directly Variant-assignable in BC AL.

    procedure IsXmlNameTable_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlNameTable());
    end;

    // ── XmlReadOptions (false-only) ────────────────────────────────────────────
    // XmlReadOptions is not directly Variant-assignable in BC AL.

    procedure IsXmlReadOptions_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlReadOptions());
    end;

    // ── XmlWriteOptions (false-only) ──────────────────────────────────────────
    // XmlWriteOptions is not directly Variant-assignable in BC AL.

    procedure IsXmlWriteOptions_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsXmlWriteOptions());
    end;

    // ── FilterPageBuilder (false-only) ────────────────────────────────────────
    // FilterPageBuilder is not directly Variant-assignable in BC AL.

    procedure IsFilterPageBuilder_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsFilterPageBuilder());
    end;

    // ── ReportFormat (false-only) ─────────────────────────────────────────────
    // ReportFormat enum is indistinguishable from NavOption in a Variant.

    procedure IsReportFormat_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsReportFormat());
    end;

    // ── PromptMode (false-only) ───────────────────────────────────────────────
    // PromptMode enum is indistinguishable from NavOption in a Variant.

    procedure IsPromptMode_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsPromptMode());
    end;
}
