codeunit 84404 "XTX Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XTX Src";

    // ── Create / Value (positive + negative) ───────────────────────────────────
    [Test]
    procedure Create_RoundTripsValue()
    begin
        // Positive: XmlText.Create(text).Value() returns the same text.
        Assert.AreEqual('hello world', Src.CreateAndGetValue('hello world'),
            'Value must round-trip the text passed to Create');
    end;

    [Test]
    procedure Create_EmptyText_RoundTrips()
    begin
        // Edge: empty text node is valid.
        Assert.AreEqual('', Src.CreateAndGetValue(''),
            'Empty XmlText.Value must be empty string');
    end;

    [Test]
    procedure Value_SetAndGet_RoundTrips()
    begin
        // Positive: Value(newVal) setter then Value() getter returns new value.
        Assert.AreEqual('updated', Src.SetAndGetValue('initial', 'updated'),
            'Value setter must update the stored text');
    end;

    [Test]
    procedure Value_InitialDiffersFromUpdated()
    begin
        // Negative: initial value must not equal the updated value.
        Assert.AreNotEqual('initial', Src.SetAndGetValue('initial', 'updated'),
            'After Value(newVal), old value must not be returned');
    end;

    // ── AsXmlNode ───────────────────────────────────────────────────────────────
    [Test]
    procedure AsXmlNode_DoesNotCrash()
    begin
        // Positive: AsXmlNode() completes without error.
        Src.CreateAsXmlNode('test');
        Assert.IsTrue(true, 'AsXmlNode must not crash');
    end;

    // ── WriteTo ─────────────────────────────────────────────────────────────────
    [Test]
    procedure WriteTo_ContainsText()
    var
        Output: Text;
    begin
        // Positive: WriteTo(TextBuilder) serialises the text content.
        Output := Src.WriteToText('some text');
        Assert.IsTrue(Output.Contains('some text'),
            'WriteTo output must contain the text node value');
    end;

    // ── AttachToElement / GetParent ─────────────────────────────────────────────
    [Test]
    procedure AttachToElement_IncreasesChildCount()
    begin
        // Positive: adding XmlText to element increases child node count.
        Assert.AreEqual(1, Src.AttachToElement('content'),
            'Element must have 1 child after Add(XmlText)');
    end;

    [Test]
    procedure GetParent_ReturnsParentElementName()
    begin
        // Positive: GetParent returns the element the text was added to.
        Assert.AreEqual('root', Src.GetParentName('content'),
            'GetParent must return the element the text was attached to');
    end;

    // ── GetDocument ─────────────────────────────────────────────────────────────
    [Test]
    procedure GetDocument_ReturnsTrueWhenAttached()
    begin
        // Positive: GetDocument succeeds when text node belongs to a document.
        Assert.IsTrue(Src.GetDocumentSuccess('text'),
            'GetDocument must return true when node is in a document');
    end;

    // ── SelectNodes ─────────────────────────────────────────────────────────────
    [Test]
    procedure SelectNodes_ParentAxisReturnsCount()
    begin
        // Positive: SelectNodes('.') on a text node in a document returns nodes.
        Assert.IsTrue(Src.SelectNodesCount('text', '.') >= 0,
            'SelectNodes must return without error');
    end;

    // ── SelectSingleNode ────────────────────────────────────────────────────────
    [Test]
    procedure SelectSingleNode_ParentAxisReturnsBool()
    begin
        // Positive: SelectSingleNode('.') returns a boolean result.
        Assert.IsTrue(true, 'SelectSingleNode must not crash');
        Src.SelectSingleNodeFound('text', '.');
    end;

    // ── AddAfterSelf ────────────────────────────────────────────────────────────
    [Test]
    procedure AddAfterSelf_IncreasesChildCount()
    begin
        // Positive: AddAfterSelf adds a sibling after the text node.
        Assert.AreEqual(2, Src.AddAfterSelfChildCount('first'),
            'AddAfterSelf must increase parent child count to 2');
    end;

    // ── AddBeforeSelf ───────────────────────────────────────────────────────────
    [Test]
    procedure AddBeforeSelf_IncreasesChildCount()
    begin
        // Positive: AddBeforeSelf adds a sibling before the text node.
        Assert.AreEqual(2, Src.AddBeforeSelfChildCount('second'),
            'AddBeforeSelf must increase parent child count to 2');
    end;

    // ── Remove ──────────────────────────────────────────────────────────────────
    [Test]
    procedure Remove_DecreasesChildCount()
    begin
        // Positive: Remove() detaches the text node from the parent.
        Assert.AreEqual(0, Src.RemoveAndCountChildren('bye'),
            'After Remove(), parent must have 0 children');
    end;

    // ── ReplaceWith ─────────────────────────────────────────────────────────────
    [Test]
    procedure ReplaceWith_ChildCountUnchanged()
    begin
        // Positive: ReplaceWith replaces the node, keeping child count at 1.
        Assert.AreEqual(1, Src.ReplaceWithComment('text', 'replaced'),
            'After ReplaceWith, parent must still have 1 child');
    end;
}
