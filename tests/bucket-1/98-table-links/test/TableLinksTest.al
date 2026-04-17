codeunit 98002 TableLinksTest
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    [Test]
    procedure TestHasLinksDefaultFalse()
    var
        Rec: Record "Links Test Table";
        Src: Codeunit TableLinksSrc;
    begin
        Assert.IsFalse(Src.HasLinks(Rec), 'new record should have no links');
    end;

    [Test]
    procedure TestAddLinkReturnsPositiveId()
    var
        Rec: Record "Links Test Table";
        Src: Codeunit TableLinksSrc;
        LinkId: Integer;
    begin
        LinkId := Src.AddLink(Rec, 'https://example.com', 'Example');
        Assert.IsTrue(LinkId > 0, 'AddLink should return a positive link ID');
    end;

    [Test]
    procedure TestAddLinkReturnsDifferentIds()
    var
        Rec: Record "Links Test Table";
        Src: Codeunit TableLinksSrc;
        Id1: Integer;
        Id2: Integer;
    begin
        Id1 := Src.AddLink(Rec, 'https://example.com/1', 'First');
        Id2 := Src.AddLink(Rec, 'https://example.com/2', 'Second');
        Assert.AreNotEqual(Id1, Id2, 'each AddLink call should return a unique ID');
    end;

    [Test]
    procedure TestHasLinksAfterAdd()
    var
        Rec: Record "Links Test Table";
        Src: Codeunit TableLinksSrc;
    begin
        Src.AddLink(Rec, 'https://example.com', 'Test');
        Assert.IsTrue(Src.HasLinks(Rec), 'HasLinks should be true after AddLink');
    end;

    [Test]
    procedure TestDeleteLinksRemovesAll()
    var
        Rec: Record "Links Test Table";
        Src: Codeunit TableLinksSrc;
    begin
        Src.AddLink(Rec, 'https://example.com', 'Test');
        Src.DeleteLinks(Rec);
        Assert.IsFalse(Src.HasLinks(Rec), 'HasLinks should be false after DeleteLinks');
    end;

    [Test]
    procedure TestDeleteLinkById()
    var
        Rec: Record "Links Test Table";
        Src: Codeunit TableLinksSrc;
        LinkId: Integer;
    begin
        LinkId := Src.AddLink(Rec, 'https://example.com', 'Test');
        Src.DeleteLink(Rec, LinkId);
        Assert.IsFalse(Src.HasLinks(Rec), 'HasLinks should be false after DeleteLink by ID');
    end;

    [Test]
    procedure TestDeleteLinkByIdPreservesOthers()
    var
        Rec: Record "Links Test Table";
        Src: Codeunit TableLinksSrc;
        Id1: Integer;
        Id2: Integer;
    begin
        Id1 := Src.AddLink(Rec, 'https://example.com/1', 'First');
        Id2 := Src.AddLink(Rec, 'https://example.com/2', 'Second');
        Src.DeleteLink(Rec, Id1);
        Assert.IsTrue(Src.HasLinks(Rec), 'second link should remain after deleting first');
    end;

    [Test]
    procedure TestCopyLinksTransfersLinks()
    var
        Source: Record "Links Test Table";
        Target: Record "Links Test Table";
        Src: Codeunit TableLinksSrc;
    begin
        Src.AddLink(Source, 'https://example.com', 'Link');
        Src.CopyLinks(Target, Source);
        Assert.IsTrue(Src.HasLinks(Target), 'Target should have links after CopyLinks');
    end;

    [Test]
    procedure TestCopyLinksAddsToExisting()
    var
        Source: Record "Links Test Table";
        Target: Record "Links Test Table";
        Src: Codeunit TableLinksSrc;
    begin
        Src.AddLink(Source, 'https://source.com', 'Source link');
        Src.AddLink(Target, 'https://target.com', 'Target existing');
        Src.CopyLinks(Target, Source);
        Assert.IsTrue(Src.HasLinks(Target), 'Target links should still exist after CopyLinks');
    end;
}
