/// Tests proving Variant.IsXxx type-check methods for XML types and misc stubs.
/// Covers issue #987.
///
/// Test strategy:
///   XmlDocumentType — true + false (XmlDocumentType is a Variant-compatible
///     XmlNode subtype; XmlDocumentType.Create('root','','','') works).
///   All other types — false only. XmlAttributeCollection, XmlNamespaceManager,
///     XmlNameTable, XmlReadOptions, XmlWriteOptions, FilterPageBuilder are not
///     directly assignable to Variant in BC AL, so the implementation always
///     returns false — which is correct. The false case proves it doesn't
///     incorrectly return true.
codeunit 60393 "VXT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "VXT Src";

    // ── XmlDocumentType ───────────────────────────────────────────────────────

    [Test]
    procedure IsXmlDocumentType_True_ForDocType()
    begin
        // [GIVEN] A Variant holding an XmlDocumentType
        // [WHEN]  IsXmlDocumentType() is called
        // [THEN]  Returns true
        Assert.IsTrue(Src.IsXmlDocumentType_ForDocType(),
            'Variant.IsXmlDocumentType must return true for XmlDocumentType');
    end;

    [Test]
    procedure IsXmlDocumentType_False_ForInteger()
    begin
        // [GIVEN] A Variant holding an Integer
        // [WHEN]  IsXmlDocumentType() is called
        // [THEN]  Returns false
        Assert.IsFalse(Src.IsXmlDocumentType_ForInteger(),
            'Variant.IsXmlDocumentType must return false for Integer');
    end;

    // ── XmlAttributeCollection (false only) ───────────────────────────────────

    [Test]
    procedure IsXmlAttributeCollection_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsXmlAttributeCollection_ForInteger(),
            'Variant.IsXmlAttributeCollection must return false for Integer');
    end;

    // ── XmlNamespaceManager (false only) ──────────────────────────────────────

    [Test]
    procedure IsXmlNamespaceManager_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsXmlNamespaceManager_ForInteger(),
            'Variant.IsXmlNamespaceManager must return false for Integer');
    end;

    // ── XmlNameTable (false only) ─────────────────────────────────────────────

    [Test]
    procedure IsXmlNameTable_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsXmlNameTable_ForInteger(),
            'Variant.IsXmlNameTable must return false for Integer');
    end;

    // ── XmlReadOptions (false only) ────────────────────────────────────────────

    [Test]
    procedure IsXmlReadOptions_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsXmlReadOptions_ForInteger(),
            'Variant.IsXmlReadOptions must return false for Integer');
    end;

    // ── XmlWriteOptions (false only) ──────────────────────────────────────────

    [Test]
    procedure IsXmlWriteOptions_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsXmlWriteOptions_ForInteger(),
            'Variant.IsXmlWriteOptions must return false for Integer');
    end;

    // ── FilterPageBuilder (false only) ────────────────────────────────────────

    [Test]
    procedure IsFilterPageBuilder_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsFilterPageBuilder_ForInteger(),
            'Variant.IsFilterPageBuilder must return false for Integer');
    end;

    // ── ReportFormat (false only) ─────────────────────────────────────────────

    [Test]
    procedure IsReportFormat_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsReportFormat_ForInteger(),
            'Variant.IsReportFormat must return false for Integer');
    end;

    // ── PromptMode (false only) ───────────────────────────────────────────────

    [Test]
    procedure IsPromptMode_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsPromptMode_ForInteger(),
            'Variant.IsPromptMode must return false for Integer');
    end;
}
