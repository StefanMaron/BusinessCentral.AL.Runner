codeunit 108002 MiscStubsTest
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    [Test]
    procedure TestXmlNodeIsDocumentTypeReturnsFalseForElement()
    var
        Doc: XmlDocument;
        Root: XmlElement;
        ElemNode: XmlNode;
        Src: Codeunit MiscStubsSrc;
    begin
        XmlDocument.ReadFrom('<root/>', Doc);
        Doc.GetRoot(Root);
        ElemNode := Root.AsXmlNode();
        Assert.IsFalse(Src.DoXmlNodeIsDocumentType(ElemNode), 'XmlElement node is not DocumentType');
    end;

    [Test]
    procedure TestXmlNodeIsDocumentTypeReturnsFalseForDocument()
    var
        Doc: XmlDocument;
        DocNode: XmlNode;
        Src: Codeunit MiscStubsSrc;
    begin
        XmlDocument.ReadFrom('<root/>', Doc);
        DocNode := Doc.AsXmlNode();
        Assert.IsFalse(Src.DoXmlNodeIsDocumentType(DocNode), 'XmlDocument node is not DocumentType');
    end;

    [Test]
    procedure TestNavAppGetArchiveRecordRefIsNoOp()
    var
        RecRef: RecordRef;
        Src: Codeunit MiscStubsSrc;
        Result: Boolean;
    begin
        Result := Src.DoNavAppGetArchiveRecordRef(18, RecRef);
        Assert.IsFalse(Result, 'GetArchiveRecordRef should leave RecRef unbound in standalone');
    end;

    [Test]
    procedure TestNavAppGetResourceIsNoOp()
    var
        IStream: InStream;
        Src: Codeunit MiscStubsSrc;
        Result: Boolean;
    begin
        Result := Src.DoNavAppGetResource('dummy.txt', IStream);
        Assert.IsTrue(Result, 'GetResource no-op should not throw');
    end;

    [Test]
    procedure TestRecordIdGetRecordReturnsUnbound()
    var
        RecId: RecordId;
        RecRef: RecordRef;
        Src: Codeunit MiscStubsSrc;
    begin
        RecRef := Src.DoRecordIdGetRecord(RecId);
        Assert.IsTrue(RecRef.IsEmpty(), 'GetRecord on blank RecordId returns unbound (empty) RecordRef in standalone mode');
    end;
}
