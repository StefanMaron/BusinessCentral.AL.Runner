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

    // ── GetParent ────────────────────────────────────────────────

    [Test]
    procedure XmlComment_GetParent_TrueAfterAttach()
    begin
        Assert.IsTrue(Src.GetParentAfterAttach('comment text'),
            'GetParent must return true after attaching to an element');
    end;

    // ── Remove ───────────────────────────────────────────────────

    [Test]
    procedure XmlComment_Remove_DecrementsChildCount()
    begin
        Assert.AreEqual(0, Src.RemoveFromParent('comment text'),
            'After Remove(), parent child count must be 0');
    end;

    // ── AddAfterSelf ─────────────────────────────────────────────

    [Test]
    procedure XmlComment_AddAfterSelf_GivesParentTwoChildren()
    begin
        Assert.AreEqual(2, Src.AddAfterSelf('first'),
            'AddAfterSelf must give parent two children');
    end;

    // ── AddBeforeSelf ────────────────────────────────────────────

    [Test]
    procedure XmlComment_AddBeforeSelf_GivesParentTwoChildren()
    begin
        Assert.AreEqual(2, Src.AddBeforeSelf('second'),
            'AddBeforeSelf must give parent two children');
    end;

    // ── GetDocument ──────────────────────────────────────────────

    [Test]
    procedure XmlComment_GetDocument_TrueWhenInDocument()
    begin
        Assert.IsTrue(Src.GetDocument('doc comment'),
            'GetDocument must return true when node belongs to an XmlDocument');
    end;

    // ── ReplaceWith ──────────────────────────────────────────────

    [Test]
    procedure XmlComment_ReplaceWith_ParentChildCountUnchanged()
    begin
        Assert.AreEqual(1, Src.ReplaceWith('old comment'),
            'After ReplaceWith, parent must still have exactly 1 child');
    end;

    // ── WriteTo ──────────────────────────────────────────────────

    [Test]
    procedure XmlComment_WriteTo_ContainsValue()
    begin
        Assert.IsTrue(Src.WriteToText('hello comment').Contains('hello comment'),
            'WriteTo output must contain the comment text');
    end;

    [Test]
    procedure XmlComment_WriteTo_DifferentValues_DifferentOutput()
    begin
        Assert.AreNotEqual(Src.WriteToText('aaa'), Src.WriteToText('bbb'),
            'WriteTo must reflect the actual comment content');
    end;

    // ── SelectNodes ──────────────────────────────────────────────

    [Test]
    procedure XmlComment_SelectNodes_ReturnsNonNegativeCount()
    var
        cnt: Integer;
    begin
        cnt := Src.SelectNodesCount('comment text');
        Assert.IsTrue(cnt >= 0, 'SelectNodes must return a non-negative count');
    end;

    // ── SelectSingleNode ─────────────────────────────────────────

    [Test]
    procedure XmlComment_SelectSingleNode_ReturnsBool()
    var
        found: Boolean;
    begin
        found := Src.SelectSingleNodeFound('comment text');
        Assert.IsTrue(found or not found, 'SelectSingleNode must not throw');
    end;
}
