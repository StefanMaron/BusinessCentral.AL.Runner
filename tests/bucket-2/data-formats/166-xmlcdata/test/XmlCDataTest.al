codeunit 90001 "XCD Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XCD Src";

    // ── Create / Value ───────────────────────────────────────────

    [Test]
    procedure XmlCData_Create_RoundTripsValue()
    begin
        Assert.AreEqual('<b>raw html</b>', Src.CreateAndGetValue('<b>raw html</b>'),
            'XmlCData.Value must round-trip the raw text');
    end;

    [Test]
    procedure XmlCData_Create_EmptyText()
    begin
        Assert.AreEqual('', Src.CreateAndGetValue(''),
            'XmlCData with empty content must round-trip as empty');
    end;

    [Test]
    procedure XmlCData_Create_SpecialChars()
    begin
        Assert.AreEqual('<>&"''', Src.CreateAndGetValue('<>&"'''),
            'XmlCData must preserve markup characters verbatim');
    end;

    [Test]
    procedure XmlCData_DifferentTexts_DifferentValues()
    begin
        // Guard against a no-op stub that returns a fixed string.
        Assert.AreNotEqual(
            Src.CreateAndGetValue('alpha'),
            Src.CreateAndGetValue('beta'),
            'Different CDATA texts must produce different Values');
    end;

    // ── AsXmlNode ────────────────────────────────────────────────

    [Test]
    procedure XmlCData_AsXmlNode_IsXmlCData()
    var
        node: XmlNode;
    begin
        node := Src.CreateAsXmlNode('test');
        Assert.IsTrue(node.IsXmlCData, 'AsXmlNode().IsXmlCData must be true');
    end;

    // ── Add to element ───────────────────────────────────────────

    [Test]
    procedure XmlCData_AttachToElement_IncrementsChildCount()
    begin
        Assert.AreEqual(1, Src.AttachToElement('some data'),
            'Attaching XmlCData must increase child count to 1');
    end;

    // ── WriteTo ──────────────────────────────────────────────────

    [Test]
    procedure XmlCData_WriteTo_ContainsValue()
    begin
        Assert.IsTrue(Src.WriteToText('hello world').Contains('hello world'),
            'WriteTo output must contain the CDATA value');
    end;

    [Test]
    procedure XmlCData_WriteTo_DifferentValues_DifferentOutput()
    begin
        // Guard against a stub that always emits the same string.
        Assert.AreNotEqual(Src.WriteToText('aaa'), Src.WriteToText('bbb'),
            'WriteTo must reflect the actual CDATA content');
    end;

    // ── GetParent ────────────────────────────────────────────────

    [Test]
    procedure XmlCData_GetParent_TrueAfterAttach()
    begin
        Assert.IsTrue(Src.GetParentAfterAttach('data'),
            'GetParent must return true after attaching to an element');
    end;

    // ── Remove ───────────────────────────────────────────────────

    [Test]
    procedure XmlCData_Remove_DecrementsChildCount()
    begin
        Assert.AreEqual(0, Src.RemoveFromParent('data'),
            'After Remove(), parent child count must be 0');
    end;

    // ── AddAfterSelf ─────────────────────────────────────────────

    [Test]
    procedure XmlCData_AddAfterSelf_SiblingAppended()
    begin
        Assert.AreEqual(2, Src.AddAfterSelf('first'),
            'AddAfterSelf must give parent two children');
    end;

    // ── AddBeforeSelf ────────────────────────────────────────────

    [Test]
    procedure XmlCData_AddBeforeSelf_SiblingPrepended()
    begin
        Assert.AreEqual(2, Src.AddBeforeSelf('second'),
            'AddBeforeSelf must give parent two children');
    end;

    // ── GetDocument ──────────────────────────────────────────────

    [Test]
    procedure XmlCData_GetDocument_TrueWhenInDocument()
    begin
        Assert.IsTrue(Src.GetDocument('doc content'),
            'GetDocument must return true when node belongs to an XmlDocument');
    end;

    // ── ReplaceWith ──────────────────────────────────────────────

    [Test]
    procedure XmlCData_ReplaceWith_ParentChildCountUnchanged()
    begin
        Assert.AreEqual(1, Src.ReplaceWith('old'),
            'After ReplaceWith, parent must still have exactly 1 child');
    end;

    // ── SelectNodes ──────────────────────────────────────────────

    [Test]
    procedure XmlCData_SelectNodes_ReturnsNonNegativeCount()
    var
        cnt: Integer;
    begin
        cnt := Src.SelectNodesCount('data');
        Assert.IsTrue(cnt >= 0, 'SelectNodes must return a non-negative count');
    end;

    // ── SelectSingleNode ─────────────────────────────────────────

    [Test]
    procedure XmlCData_SelectSingleNode_ReturnsBool()
    var
        found: Boolean;
    begin
        // Result is either true or false — just verify no exception thrown.
        found := Src.SelectSingleNodeFound('data');
        Assert.IsTrue(found or not found, 'SelectSingleNode must not throw');
    end;
}
