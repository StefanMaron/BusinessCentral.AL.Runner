codeunit 101001 "XDT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XDT Src";

    [Test]
    procedure WriteTo_ContainsDocTypeName()
    var
        DocType: XmlDocumentType;
        Result: Text;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        Result := Src.WriteToText(DocType);
        Assert.IsTrue(Result.Contains('root'), 'WriteTo must include the doctype name');
    end;

    [Test]
    procedure AsXmlNode_IsXmlDocumentType()
    var
        DocType: XmlDocumentType;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        Assert.IsTrue(Src.AsXmlNodeIsDocType(DocType), 'AsXmlNode result must satisfy IsXmlDocumentType()');
    end;

    [Test]
    procedure GetDocument_StandaloneDocType_ReturnsFalse()
    var
        DocType: XmlDocumentType;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        Assert.IsFalse(Src.GetDocumentReturnsTrue(DocType), 'standalone DocType has no document — must return false');
    end;

    [Test]
    procedure GetParent_StandaloneDocType_ReturnsFalse()
    var
        DocType: XmlDocumentType;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        Assert.IsFalse(Src.GetParentElementReturnsFalse(DocType), 'standalone DocType has no parent — must return false');
    end;

    [Test]
    procedure SelectNodes_ReturnsEmptyCount()
    begin
        Assert.AreEqual(0, Src.SelectNodesCount(), 'SelectNodes on doctype with no matching nodes must return 0');
    end;

    [Test]
    procedure SelectSingleNode_NotFound_ReturnsFalse()
    var
        DocType: XmlDocumentType;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        Assert.IsFalse(Src.SelectSingleNodeFound(DocType), 'SelectSingleNode must return false when node not found');
    end;

    [Test]
    procedure Remove_DetachesFromDocument()
    var
        Doc: XmlDocument;
        DocType: XmlDocumentType;
    begin
        Doc := XmlDocument.Create();
        DocType := XmlDocumentType.Create('root', '', '', '');
        Doc.Add(DocType);
        Assert.IsFalse(Src.RemoveFromDocument(Doc), 'after Remove, GetDocumentType must return false');
    end;

    [Test]
    procedure ReplaceWith_DocTypeGone()
    begin
        Assert.IsFalse(Src.ReplaceWith_DocTypeGone(), 'ReplaceWith must remove the DocType from the document');
    end;

    [Test]
    procedure AddAfterSelf_ChildCountIncreases()
    begin
        Assert.AreEqual(3, Src.AddAfterSelf_ChildCountIncreases(), 'AddAfterSelf must add a sibling after the doctype');
    end;

    [Test]
    procedure AddBeforeSelf_ChildCountIncreases()
    begin
        Assert.AreEqual(3, Src.AddBeforeSelf_ChildCountIncreases(), 'AddBeforeSelf must add a sibling before the doctype');
    end;
}
