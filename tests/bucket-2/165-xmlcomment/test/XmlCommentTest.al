codeunit 59781 "XCM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XCM Src";

    [Test]
    procedure XmlComment_Create_RoundTripsValue()
    begin
        // Positive: XmlComment.Create(text).Value returns the same text.
        Assert.AreEqual('my comment', Src.CreateAndGetValue('my comment'),
            'XmlComment.Value must round-trip the text');
    end;

    [Test]
    procedure XmlComment_Create_EmptyText()
    begin
        // Edge: empty comment body is allowed.
        Assert.AreEqual('', Src.CreateAndGetValue(''),
            'XmlComment with empty body must round-trip as empty');
    end;

    [Test]
    procedure XmlComment_Create_SpecialChars()
    begin
        // Special chars (UTF-8 / punctuation) must round-trip.
        Assert.AreEqual('caf\u00e9 & snowman \u2603', Src.CreateAndGetValue('caf\u00e9 & snowman \u2603'),
            'XmlComment must preserve special characters');
    end;

    [Test]
    procedure XmlComment_AsXmlNode_IsXmlComment()
    var
        node: XmlNode;
    begin
        // Positive: AsXmlNode returns a valid XmlNode whose underlying type is XmlComment.
        node := Src.CreateAsXmlNode('n');
        Assert.IsTrue(node.IsXmlComment, 'AsXmlNode().IsXmlComment must be true for a comment');
    end;

    [Test]
    procedure XmlComment_AttachToElement_IncrementsChildCount()
    begin
        // Proving: adding a comment to an element brings child-node count to 1.
        Assert.AreEqual(1, Src.AttachToElement('comment'),
            'Attaching a comment must increase child count to 1');
    end;

    [Test]
    procedure XmlComment_DifferentTexts_DifferentValues_NegativeTrap()
    begin
        // Negative trap: guard against a stub that returns a fixed string.
        Assert.AreNotEqual(
            Src.CreateAndGetValue('alpha'),
            Src.CreateAndGetValue('beta'),
            'Different comment texts must produce different Values');
    end;
}
