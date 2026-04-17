/// Tests proving Variant.IsXxx type-check methods for XML types and enum stubs.
/// Covers issue #987: stubs existed but had no proof tests.
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

    // ── XmlAttributeCollection ────────────────────────────────────────────────

    [Test]
    procedure IsXmlAttributeCollection_True_ForAttrCollection()
    begin
        // [GIVEN] A Variant holding an XmlAttributeCollection
        // [WHEN]  IsXmlAttributeCollection() is called
        // [THEN]  Returns true
        Assert.IsTrue(Src.IsXmlAttributeCollection_ForAttrCollection(),
            'Variant.IsXmlAttributeCollection must return true for XmlAttributeCollection');
    end;

    [Test]
    procedure IsXmlAttributeCollection_False_ForInteger()
    begin
        // [GIVEN] A Variant holding an Integer
        // [WHEN]  IsXmlAttributeCollection() is called
        // [THEN]  Returns false
        Assert.IsFalse(Src.IsXmlAttributeCollection_ForInteger(),
            'Variant.IsXmlAttributeCollection must return false for Integer');
    end;

    // ── XmlNamespaceManager ───────────────────────────────────────────────────

    [Test]
    procedure IsXmlNamespaceManager_True_ForNsMgr()
    begin
        // [GIVEN] A Variant holding an XmlNamespaceManager
        // [WHEN]  IsXmlNamespaceManager() is called
        // [THEN]  Returns true
        Assert.IsTrue(Src.IsXmlNamespaceManager_ForNsMgr(),
            'Variant.IsXmlNamespaceManager must return true for XmlNamespaceManager');
    end;

    [Test]
    procedure IsXmlNamespaceManager_False_ForInteger()
    begin
        // [GIVEN] A Variant holding an Integer
        // [WHEN]  IsXmlNamespaceManager() is called
        // [THEN]  Returns false
        Assert.IsFalse(Src.IsXmlNamespaceManager_ForInteger(),
            'Variant.IsXmlNamespaceManager must return false for Integer');
    end;

    // ── XmlNameTable ──────────────────────────────────────────────────────────

    [Test]
    procedure IsXmlNameTable_True_ForNameTable()
    begin
        // [GIVEN] A Variant holding an XmlNameTable
        // [WHEN]  IsXmlNameTable() is called
        // [THEN]  Returns true
        Assert.IsTrue(Src.IsXmlNameTable_ForNameTable(),
            'Variant.IsXmlNameTable must return true for XmlNameTable');
    end;

    [Test]
    procedure IsXmlNameTable_False_ForInteger()
    begin
        // [GIVEN] A Variant holding an Integer
        // [WHEN]  IsXmlNameTable() is called
        // [THEN]  Returns false
        Assert.IsFalse(Src.IsXmlNameTable_ForInteger(),
            'Variant.IsXmlNameTable must return false for Integer');
    end;

    // ── XmlReadOptions ────────────────────────────────────────────────────────

    [Test]
    procedure IsXmlReadOptions_True_ForReadOptions()
    begin
        // [GIVEN] A Variant holding an XmlReadOptions
        // [WHEN]  IsXmlReadOptions() is called
        // [THEN]  Returns true
        Assert.IsTrue(Src.IsXmlReadOptions_ForReadOptions(),
            'Variant.IsXmlReadOptions must return true for XmlReadOptions');
    end;

    [Test]
    procedure IsXmlReadOptions_False_ForInteger()
    begin
        // [GIVEN] A Variant holding an Integer
        // [WHEN]  IsXmlReadOptions() is called
        // [THEN]  Returns false
        Assert.IsFalse(Src.IsXmlReadOptions_ForInteger(),
            'Variant.IsXmlReadOptions must return false for Integer');
    end;

    // ── XmlWriteOptions ───────────────────────────────────────────────────────

    [Test]
    procedure IsXmlWriteOptions_True_ForWriteOptions()
    begin
        // [GIVEN] A Variant holding an XmlWriteOptions
        // [WHEN]  IsXmlWriteOptions() is called
        // [THEN]  Returns true
        Assert.IsTrue(Src.IsXmlWriteOptions_ForWriteOptions(),
            'Variant.IsXmlWriteOptions must return true for XmlWriteOptions');
    end;

    [Test]
    procedure IsXmlWriteOptions_False_ForInteger()
    begin
        // [GIVEN] A Variant holding an Integer
        // [WHEN]  IsXmlWriteOptions() is called
        // [THEN]  Returns false
        Assert.IsFalse(Src.IsXmlWriteOptions_ForInteger(),
            'Variant.IsXmlWriteOptions must return false for Integer');
    end;

    // ── FilterPageBuilder ─────────────────────────────────────────────────────

    [Test]
    procedure IsFilterPageBuilder_True_ForFPB()
    begin
        // [GIVEN] A Variant holding a FilterPageBuilder
        // [WHEN]  IsFilterPageBuilder() is called
        // [THEN]  Returns true
        Assert.IsTrue(Src.IsFilterPageBuilder_ForFPB(),
            'Variant.IsFilterPageBuilder must return true for FilterPageBuilder');
    end;

    [Test]
    procedure IsFilterPageBuilder_False_ForInteger()
    begin
        // [GIVEN] A Variant holding an Integer
        // [WHEN]  IsFilterPageBuilder() is called
        // [THEN]  Returns false
        Assert.IsFalse(Src.IsFilterPageBuilder_ForInteger(),
            'Variant.IsFilterPageBuilder must return false for Integer');
    end;

    // ── ReportFormat (always false — enum indistinguishable from Integer in Variant) ──

    [Test]
    procedure IsReportFormat_False_ForInteger()
    begin
        // [GIVEN] A Variant holding an Integer
        // [WHEN]  IsReportFormat() is called
        // [THEN]  Returns false (not a ReportFormat value)
        Assert.IsFalse(Src.IsReportFormat_ForInteger(),
            'Variant.IsReportFormat must return false for Integer');
    end;

    // ── PromptMode (always false — enum indistinguishable from Integer in Variant) ──

    [Test]
    procedure IsPromptMode_False_ForInteger()
    begin
        // [GIVEN] A Variant holding an Integer
        // [WHEN]  IsPromptMode() is called
        // [THEN]  Returns false (not a PromptMode value)
        Assert.IsFalse(Src.IsPromptMode_ForInteger(),
            'Variant.IsPromptMode must return false for Integer');
    end;
}
