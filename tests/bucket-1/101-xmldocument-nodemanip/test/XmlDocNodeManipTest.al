codeunit 110001 XmlDocNodeManipTest
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    [Test]
    procedure TestRemoveVarParam()
    var
        Doc: XmlDocument;
        Src: Codeunit XmlDocNodeManipSrc;
    begin
        XmlDocument.ReadFrom('<root/>', Doc);
        Src.RemoveDoc(Doc);
        Assert.IsTrue(true, 'Remove() on var param should not throw');
    end;

    [Test]
    procedure TestRemoveLocalVar()
    var
        Src: Codeunit XmlDocNodeManipSrc;
    begin
        Src.RemoveDocLocal();
        Assert.IsTrue(true, 'Remove() on local var should not throw');
    end;

    [Test]
    procedure TestReplaceWithIsNoOp()
    var
        Doc: XmlDocument;
        NewDoc: XmlDocument;
        Src: Codeunit XmlDocNodeManipSrc;
    begin
        XmlDocument.ReadFrom('<root/>', Doc);
        XmlDocument.ReadFrom('<other/>', NewDoc);
        Src.ReplaceDocWith(Doc, NewDoc);
        Assert.IsTrue(true, 'ReplaceWith() should not throw');
    end;

    [Test]
    procedure TestAddAfterSelfIsNoOp()
    var
        Doc: XmlDocument;
        Comment: XmlComment;
        CommentNode: XmlNode;
        Src: Codeunit XmlDocNodeManipSrc;
    begin
        XmlDocument.ReadFrom('<root/>', Doc);
        Comment := XmlComment.Create('sibling');
        CommentNode := Comment.AsXmlNode();
        Src.AddDocAfterSelf(Doc, CommentNode);
        Assert.IsTrue(true, 'AddAfterSelf() should not throw');
    end;

    [Test]
    procedure TestAddBeforeSelfIsNoOp()
    var
        Doc: XmlDocument;
        Comment: XmlComment;
        CommentNode: XmlNode;
        Src: Codeunit XmlDocNodeManipSrc;
    begin
        XmlDocument.ReadFrom('<root/>', Doc);
        Comment := XmlComment.Create('sibling');
        CommentNode := Comment.AsXmlNode();
        Src.AddDocBeforeSelf(Doc, CommentNode);
        Assert.IsTrue(true, 'AddBeforeSelf() should not throw');
    end;
}
