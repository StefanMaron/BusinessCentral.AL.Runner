/// Tests for Xml*.WriteTo per-format overloads — issue #1370.
/// Proves WriteTo(var Text), WriteTo(XmlWriteOptions, var Text), and
/// WriteTo(XmlWriteOptions, var OutStream) across representative Xml node types.
codeunit 309302 "XWT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XWT Src";

    // ── XmlDocument.WriteTo(var Text) ─────────────────────────────────────────

    [Test]
    procedure XmlDoc_WriteToText_ContainsElemName()
    begin
        // [GIVEN] XmlDocument with root element 'invoice'
        // [WHEN] WriteTo(var Text) is called
        // [THEN] output contains 'invoice'
        Assert.IsTrue(
            Src.XmlDocWriteToText('invoice').Contains('invoice'),
            'XmlDocument.WriteTo(Text) must contain the root element name');
    end;

    [Test]
    procedure XmlDoc_WriteToText_NotEmpty()
    begin
        Assert.AreNotEqual('', Src.XmlDocWriteToText('root'),
            'XmlDocument.WriteTo(Text) must produce non-empty output');
    end;

    // ── XmlDocument.WriteTo(XmlWriteOptions, var Text) ────────────────────────

    [Test]
    procedure XmlDoc_WriteToOptionsText_ContainsElemName()
    begin
        // [GIVEN] XmlDocument with root element 'order'
        // [WHEN] WriteTo(XmlWriteOptions, var Text) is called
        // [THEN] output contains 'order'
        Assert.IsTrue(
            Src.XmlDocWriteToOptionsText('order').Contains('order'),
            'XmlDocument.WriteTo(XmlWriteOptions, Text) must contain the root element name');
    end;

    [Test]
    procedure XmlDoc_WriteToOptionsText_NotEmpty()
    begin
        Assert.AreNotEqual('', Src.XmlDocWriteToOptionsText('root'),
            'XmlDocument.WriteTo(XmlWriteOptions, Text) must produce non-empty output');
    end;

    // ── XmlDocument.WriteTo(XmlWriteOptions, var OutStream) ───────────────────

    [Test]
    procedure XmlDoc_WriteToOptionsStream_ContainsElemName()
    begin
        // [GIVEN] XmlDocument with root element 'shipment'
        // [WHEN] WriteTo(XmlWriteOptions, var OutStream) is called
        // [THEN] stream content contains 'shipment'
        Assert.IsTrue(
            Src.XmlDocWriteToOptionsStream('shipment').Contains('shipment'),
            'XmlDocument.WriteTo(XmlWriteOptions, OutStream) must contain the root element name');
    end;

    [Test]
    procedure XmlDoc_WriteToOptionsStream_NotEmpty()
    begin
        Assert.AreNotEqual('', Src.XmlDocWriteToOptionsStream('root'),
            'XmlDocument.WriteTo(XmlWriteOptions, OutStream) must produce non-empty output');
    end;

    // ── XmlElement.WriteTo(var Text) ──────────────────────────────────────────

    [Test]
    procedure XmlElem_WriteToText_ContainsElemName()
    begin
        Assert.IsTrue(
            Src.XmlElemWriteToText('lineitem').Contains('lineitem'),
            'XmlElement.WriteTo(Text) must contain the element name');
    end;

    // ── XmlElement.WriteTo(XmlWriteOptions, var Text) ─────────────────────────

    [Test]
    procedure XmlElem_WriteToOptionsText_ContainsElemName()
    begin
        Assert.IsTrue(
            Src.XmlElemWriteToOptionsText('lineitem').Contains('lineitem'),
            'XmlElement.WriteTo(XmlWriteOptions, Text) must contain the element name');
    end;

    // ── XmlElement.WriteTo(XmlWriteOptions, var OutStream) ────────────────────

    [Test]
    procedure XmlElem_WriteToOptionsStream_ContainsElemName()
    begin
        Assert.IsTrue(
            Src.XmlElemWriteToOptionsStream('lineitem').Contains('lineitem'),
            'XmlElement.WriteTo(XmlWriteOptions, OutStream) must contain the element name');
    end;

    // ── XmlCData.WriteTo(var Text) ────────────────────────────────────────────

    [Test]
    procedure XmlCData_WriteToText_ContainsCDataMarker()
    begin
        // CDATA serialization must produce <![CDATA[...]]> wrapper
        Assert.IsTrue(
            Src.XmlCDataWriteToText('hello').Contains('CDATA'),
            'XmlCData.WriteTo(Text) must produce CDATA-wrapped output');
    end;

    [Test]
    procedure XmlCData_WriteToText_ContainsValue()
    begin
        Assert.IsTrue(
            Src.XmlCDataWriteToText('mydata').Contains('mydata'),
            'XmlCData.WriteTo(Text) must contain the CDATA value');
    end;

    // ── XmlCData.WriteTo(XmlWriteOptions, var Text) ───────────────────────────

    [Test]
    procedure XmlCData_WriteToOptionsText_ContainsValue()
    begin
        Assert.IsTrue(
            Src.XmlCDataWriteToOptionsText('mydata').Contains('mydata'),
            'XmlCData.WriteTo(XmlWriteOptions, Text) must contain the CDATA value');
    end;

    // ── XmlComment.WriteTo(var Text) ──────────────────────────────────────────

    [Test]
    procedure XmlComment_WriteToText_ContainsCommentMarker()
    begin
        Assert.IsTrue(
            Src.XmlCommentWriteToText('note').Contains('<!--'),
            'XmlComment.WriteTo(Text) must produce comment-wrapped output');
    end;

    [Test]
    procedure XmlComment_WriteToText_ContainsValue()
    begin
        Assert.IsTrue(
            Src.XmlCommentWriteToText('notetext').Contains('notetext'),
            'XmlComment.WriteTo(Text) must contain the comment text');
    end;

    // ── XmlComment.WriteTo(XmlWriteOptions, var Text) ─────────────────────────

    [Test]
    procedure XmlComment_WriteToOptionsText_ContainsValue()
    begin
        Assert.IsTrue(
            Src.XmlCommentWriteToOptionsText('notetext').Contains('notetext'),
            'XmlComment.WriteTo(XmlWriteOptions, Text) must contain the comment text');
    end;

    // ── XmlDeclaration.WriteTo(var Text) ──────────────────────────────────────

    [Test]
    procedure XmlDecl_WriteToText_ContainsVersion()
    begin
        Assert.IsTrue(
            Src.XmlDeclWriteToText().Contains('1.0'),
            'XmlDeclaration.WriteTo(Text) must contain the version number');
    end;

    // ── XmlDeclaration.WriteTo(XmlWriteOptions, var Text) ─────────────────────

    [Test]
    procedure XmlDecl_WriteToOptionsText_ContainsVersion()
    begin
        Assert.IsTrue(
            Src.XmlDeclWriteToOptionsText().Contains('1.0'),
            'XmlDeclaration.WriteTo(XmlWriteOptions, Text) must contain the version number');
    end;

    // ── XmlText.WriteTo(var Text) ─────────────────────────────────────────────

    [Test]
    procedure XmlText_WriteToText_ContainsValue()
    begin
        Assert.IsTrue(
            Src.XmlTextWriteToText('helloworld').Contains('helloworld'),
            'XmlText.WriteTo(Text) must contain the text value');
    end;

    // ── XmlText.WriteTo(XmlWriteOptions, var Text) ────────────────────────────

    [Test]
    procedure XmlText_WriteToOptionsText_ContainsValue()
    begin
        Assert.IsTrue(
            Src.XmlTextWriteToOptionsText('helloworld').Contains('helloworld'),
            'XmlText.WriteTo(XmlWriteOptions, Text) must contain the text value');
    end;

    // ── XmlAttribute.WriteTo(var Text) ────────────────────────────────────────

    [Test]
    procedure XmlAttr_WriteToText_ContainsAttrName()
    begin
        Assert.IsTrue(
            Src.XmlAttrWriteToText('id', '42').Contains('id'),
            'XmlAttribute.WriteTo(Text) must contain the attribute name');
    end;

    [Test]
    procedure XmlAttr_WriteToText_ContainsAttrValue()
    begin
        Assert.IsTrue(
            Src.XmlAttrWriteToText('id', '42').Contains('42'),
            'XmlAttribute.WriteTo(Text) must contain the attribute value');
    end;

    // ── XmlAttribute.WriteTo(XmlWriteOptions, var Text) ───────────────────────

    [Test]
    procedure XmlAttr_WriteToOptionsText_ContainsAttrValue()
    begin
        Assert.IsTrue(
            Src.XmlAttrWriteToOptionsText('id', '42').Contains('42'),
            'XmlAttribute.WriteTo(XmlWriteOptions, Text) must contain the attribute value');
    end;

    // ── XmlProcessingInstruction.WriteTo(var Text) ────────────────────────────

    [Test]
    procedure XmlPI_WriteToText_ContainsTarget()
    begin
        Assert.IsTrue(
            Src.XmlPIWriteToText('xml-stylesheet', 'type="text/xsl"').Contains('xml-stylesheet'),
            'XmlProcessingInstruction.WriteTo(Text) must contain the target');
    end;

    // ── XmlProcessingInstruction.WriteTo(XmlWriteOptions, var Text) ───────────

    [Test]
    procedure XmlPI_WriteToOptionsText_ContainsTarget()
    begin
        Assert.IsTrue(
            Src.XmlPIWriteToOptionsText('xml-stylesheet', 'type="text/xsl"').Contains('xml-stylesheet'),
            'XmlProcessingInstruction.WriteTo(XmlWriteOptions, Text) must contain the target');
    end;

    // ── XmlNode.WriteTo(var Text) ─────────────────────────────────────────────

    [Test]
    procedure XmlNode_WriteToText_ContainsElemName()
    begin
        Assert.IsTrue(
            Src.XmlNodeWriteToText('product').Contains('product'),
            'XmlNode.WriteTo(Text) must contain the element name when node is an element');
    end;

    // ── XmlNode.WriteTo(XmlWriteOptions, var Text) ────────────────────────────

    [Test]
    procedure XmlNode_WriteToOptionsText_ContainsElemName()
    begin
        Assert.IsTrue(
            Src.XmlNodeWriteToOptionsText('product').Contains('product'),
            'XmlNode.WriteTo(XmlWriteOptions, Text) must contain the element name');
    end;
}
