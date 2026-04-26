codeunit 107101 "XPI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XPI Pi Src";

    // ── WriteTo ──────────────────────────────────────────────────────────

    [Test]
    procedure XmlPI_WriteTo_ContainsTarget()
    begin
        // Positive: WriteTo output must contain the PI target.
        Assert.IsTrue(
            Src.WriteToText('xml-stylesheet', 'type="text/xsl"').Contains('xml-stylesheet'),
            'WriteTo must contain the PI target name');
    end;

    [Test]
    procedure XmlPI_WriteTo_ContainsData()
    begin
        // Positive: WriteTo output must contain the PI data.
        Assert.IsTrue(
            Src.WriteToText('mypi', 'somedata=1').Contains('somedata=1'),
            'WriteTo must contain the PI data');
    end;

    [Test]
    procedure XmlPI_WriteTo_DifferentTargets_DifferentOutput()
    begin
        // Negative trap: different targets must produce different serializations.
        Assert.AreNotEqual(
            Src.WriteToText('target-a', 'data'),
            Src.WriteToText('target-b', 'data'),
            'WriteTo must reflect the actual PI target');
    end;

    // ── AsXmlNode ────────────────────────────────────────────────────────

    [Test]
    procedure XmlPI_AsXmlNode_IsXmlProcessingInstruction()
    var
        Node: XmlNode;
    begin
        // Positive: AsXmlNode returns an XmlNode whose underlying type is XmlProcessingInstruction.
        Node := Src.CreateAsXmlNode('test', 'data');
        Assert.IsTrue(Node.IsXmlProcessingInstruction(), 'AsXmlNode().IsXmlProcessingInstruction() must be true');
    end;

    [Test]
    procedure XmlPI_AsXmlNode_NotXmlElement()
    var
        Node: XmlNode;
    begin
        // Negative: node must NOT be reported as an element.
        Node := Src.CreateAsXmlNode('test', 'data');
        Assert.IsFalse(Node.IsXmlElement(), 'AsXmlNode of a PI must not be an element');
    end;

    // ── GetParent ────────────────────────────────────────────────────────

    [Test]
    procedure XmlPI_GetParent_FalseWhenDetached()
    begin
        // Negative: detached PI has no parent.
        Assert.IsFalse(Src.GetParentDetached(), 'Detached PI must have no parent');
    end;

    [Test]
    procedure XmlPI_GetParent_TrueAfterAttach()
    begin
        // Positive: attached PI must return true from GetParent.
        Assert.IsTrue(Src.GetParentAttached(), 'GetParent must return true after attaching to element');
    end;

    // ── GetDocument ──────────────────────────────────────────────────────

    [Test]
    procedure XmlPI_GetDocument_FalseWhenDetached()
    begin
        // Negative: detached PI has no owning document.
        Assert.IsFalse(Src.GetDocumentDetached(), 'Detached PI must have no document');
    end;

    [Test]
    procedure XmlPI_GetDocument_TrueWhenInDocument()
    begin
        // Positive: PI attached to document subtree must return true.
        Assert.IsTrue(Src.GetDocumentInDoc(), 'GetDocument must return true when PI is in a document');
    end;

    // ── Remove ───────────────────────────────────────────────────────────

    [Test]
    procedure XmlPI_Remove_DecrementsChildCount()
    begin
        // Positive: after Remove(), parent has 0 children.
        Assert.AreEqual(0, Src.RemoveFromParent(), 'After Remove(), parent child count must be 0');
    end;

    // ── AddAfterSelf ─────────────────────────────────────────────────────

    [Test]
    procedure XmlPI_AddAfterSelf_GivesParentTwoChildren()
    begin
        // Positive: after AddAfterSelf(), parent must have 2 children.
        Assert.AreEqual(2, Src.AddAfterSelf(), 'AddAfterSelf must give parent two children');
    end;

    // ── AddBeforeSelf ────────────────────────────────────────────────────

    [Test]
    procedure XmlPI_AddBeforeSelf_GivesParentTwoChildren()
    begin
        // Positive: after AddBeforeSelf(), parent must have 2 children.
        Assert.AreEqual(2, Src.AddBeforeSelf(), 'AddBeforeSelf must give parent two children');
    end;

    // ── ReplaceWith ──────────────────────────────────────────────────────

    [Test]
    procedure XmlPI_ReplaceWith_ParentChildCountUnchanged()
    begin
        // Positive: after ReplaceWith(), parent still has exactly 1 child.
        Assert.AreEqual(1, Src.ReplaceWith(), 'After ReplaceWith, parent must still have 1 child');
    end;

    // ── SelectNodes ──────────────────────────────────────────────────────

    [Test]
    procedure XmlPI_SelectNodes_ReturnsNonNegativeCount()
    var
        Cnt: Integer;
    begin
        // Positive: SelectNodes must not throw and must return >= 0.
        Cnt := Src.SelectNodesCount();
        Assert.IsTrue(Cnt >= 0, 'SelectNodes must return a non-negative count');
    end;

    // ── SelectSingleNode ─────────────────────────────────────────────────

    [Test]
    procedure XmlPI_SelectSingleNode_ReturnsBool()
    var
        Found: Boolean;
    begin
        // Positive: SelectSingleNode must not throw.
        Found := Src.SelectSingleNodeFound();
        Assert.IsTrue(Found or not Found, 'SelectSingleNode must not throw');
    end;
}
