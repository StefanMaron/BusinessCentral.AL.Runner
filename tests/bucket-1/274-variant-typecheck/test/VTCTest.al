codeunit 116002 "VTC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "VTC Src";

    // ── XmlDocument ────────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlDocument_TrueForXmlDocument()
    begin
        Assert.IsTrue(Src.IsXmlDocument_ForDoc(),
            'IsXmlDocument must be true when Variant holds XmlDocument');
    end;

    [Test]
    procedure VTC_IsXmlDocument_FalseForInteger()
    begin
        Assert.IsFalse(Src.IsXmlDocument_ForInteger(),
            'IsXmlDocument must be false when Variant holds Integer');
    end;

    // ── XmlElement ────────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlElement_TrueForXmlElement()
    begin
        Assert.IsTrue(Src.IsXmlElement_ForElement(),
            'IsXmlElement must be true when Variant holds XmlElement');
    end;

    [Test]
    procedure VTC_IsXmlElement_FalseForXmlDocument()
    begin
        Assert.IsFalse(Src.IsXmlElement_ForDoc(),
            'IsXmlElement must be false when Variant holds XmlDocument');
    end;

    // ── XmlNode ────────────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlNode_TrueForXmlNode()
    begin
        Assert.IsTrue(Src.IsXmlNode_ForElement(),
            'IsXmlNode must be true when Variant holds XmlNode');
    end;

    [Test]
    procedure VTC_IsXmlNode_FalseForInteger()
    begin
        Assert.IsFalse(Src.IsXmlNode_ForInteger(),
            'IsXmlNode must be false when Variant holds Integer');
    end;

    // ── XmlProcessingInstruction ──────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlPI_TrueForPI()
    begin
        Assert.IsTrue(Src.IsXmlPI_ForPI(),
            'IsXmlProcessingInstruction must be true when Variant holds XmlProcessingInstruction');
    end;

    [Test]
    procedure VTC_IsXmlPI_FalseForElement()
    begin
        Assert.IsFalse(Src.IsXmlPI_ForElement(),
            'IsXmlProcessingInstruction must be false when Variant holds XmlElement');
    end;

    // ── XmlCData ──────────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlCData_TrueForCData()
    begin
        Assert.IsTrue(Src.IsXmlCData_ForCData(),
            'IsXmlCData must be true when Variant holds XmlCData');
    end;

    [Test]
    procedure VTC_IsXmlCData_FalseForText()
    begin
        Assert.IsFalse(Src.IsXmlCData_ForText(),
            'IsXmlCData must be false when Variant holds Text');
    end;

    // ── XmlComment ────────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlComment_TrueForComment()
    begin
        Assert.IsTrue(Src.IsXmlComment_ForComment(),
            'IsXmlComment must be true when Variant holds XmlComment');
    end;

    [Test]
    procedure VTC_IsXmlComment_FalseForText()
    begin
        Assert.IsFalse(Src.IsXmlComment_ForText(),
            'IsXmlComment must be false when Variant holds Text');
    end;

    // ── XmlDeclaration ────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlDeclaration_TrueForDeclaration()
    begin
        Assert.IsTrue(Src.IsXmlDeclaration_ForDecl(),
            'IsXmlDeclaration must be true when Variant holds XmlDeclaration');
    end;

    [Test]
    procedure VTC_IsXmlDeclaration_FalseForText()
    begin
        Assert.IsFalse(Src.IsXmlDeclaration_ForText(),
            'IsXmlDeclaration must be false when Variant holds Text');
    end;

    // ── XmlText ───────────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlText_TrueForXmlText()
    begin
        Assert.IsTrue(Src.IsXmlText_ForXmlText(),
            'IsXmlText must be true when Variant holds XmlText');
    end;

    [Test]
    procedure VTC_IsXmlText_FalseForText()
    begin
        Assert.IsFalse(Src.IsXmlText_ForText(),
            'IsXmlText must be false when Variant holds AL Text');
    end;

    // ── XmlNodeList ───────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlNodeList_TrueForNodeList()
    begin
        Assert.IsTrue(Src.IsXmlNodeList_ForNodeList(),
            'IsXmlNodeList must be true when Variant holds XmlNodeList');
    end;

    [Test]
    procedure VTC_IsXmlNodeList_FalseForText()
    begin
        Assert.IsFalse(Src.IsXmlNodeList_ForText(),
            'IsXmlNodeList must be false when Variant holds Text');
    end;

    // ── XmlAttribute ──────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsXmlAttribute_TrueForAttribute()
    begin
        Assert.IsTrue(Src.IsXmlAttribute_ForAttr(),
            'IsXmlAttribute must be true when Variant holds XmlAttribute');
    end;

    [Test]
    procedure VTC_IsXmlAttribute_FalseForText()
    begin
        Assert.IsFalse(Src.IsXmlAttribute_ForText(),
            'IsXmlAttribute must be false when Variant holds Text');
    end;

    // ── InStream ──────────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsInStream_TrueForInStream()
    begin
        Assert.IsTrue(Src.IsInStream_ForInStream(),
            'IsInStream must be true when Variant holds InStream');
    end;

    [Test]
    procedure VTC_IsInStream_FalseForText()
    begin
        Assert.IsFalse(Src.IsInStream_ForText(),
            'IsInStream must be false when Variant holds Text');
    end;

    // ── OutStream ─────────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsOutStream_TrueForOutStream()
    begin
        Assert.IsTrue(Src.IsOutStream_ForOutStream(),
            'IsOutStream must be true when Variant holds OutStream');
    end;

    [Test]
    procedure VTC_IsOutStream_FalseForText()
    begin
        Assert.IsFalse(Src.IsOutStream_ForText(),
            'IsOutStream must be false when Variant holds Text');
    end;

    // ── IsDictionary ──────────────────────────────────────────────────────────

    [Test]
    procedure VTC_IsDictionary_TrueForDictionary()
    begin
        Assert.IsTrue(Src.IsDictionary_ForDict(),
            'IsDictionary must be true when Variant holds Dictionary of [Text, Text]');
    end;

    [Test]
    procedure VTC_IsDictionary_FalseForText()
    begin
        Assert.IsFalse(Src.IsDictionary_ForText(),
            'IsDictionary must be false when Variant holds Text');
    end;
}
