codeunit 60261 "XPI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XPI Src";

    [Test]
    procedure Create_GetTarget()
    begin
        Assert.AreEqual('xml-stylesheet', Src.CreateAndGetTarget(),
            'XmlProcessingInstruction.GetTarget must return the created target');
    end;

    [Test]
    procedure Create_GetData()
    begin
        Assert.AreEqual('type="text/css"', Src.CreateAndGetData(),
            'XmlProcessingInstruction.GetData must return the created data');
    end;

    [Test]
    procedure SetTarget_RoundTrip()
    begin
        Assert.AreEqual('newTarget', Src.SetTargetAndRead('newTarget'),
            'SetTarget must update and round-trip through GetTarget');
    end;

    [Test]
    procedure SetData_RoundTrip()
    begin
        Assert.AreEqual('href="style.css"', Src.SetDataAndRead('href="style.css"'),
            'SetData must update and round-trip through GetData');
    end;

    [Test]
    procedure Target_NotData_NegativeTrap()
    begin
        Assert.AreNotEqual(Src.CreateAndGetTarget(), Src.CreateAndGetData(),
            'GetTarget and GetData must not alias');
    end;

    // ── WriteTo ──────────────────────────────────────────────────────────────────

    [Test]
    procedure WriteTo_ContainsTarget()
    var
        result: Text;
    begin
        // [GIVEN] A PI with a known target
        // [WHEN] WriteTo serializes it
        result := Src.WriteToText('xml-stylesheet', 'type="text/css"');
        // [THEN] The serialized text contains the target name
        Assert.IsTrue(result.Contains('xml-stylesheet'),
            'WriteTo must serialize the PI target into the output text');
    end;

    [Test]
    procedure WriteTo_ContainsData()
    var
        result: Text;
    begin
        // [GIVEN] A PI with known data
        // [WHEN] WriteTo serializes it
        result := Src.WriteToText('target', 'href="style.css"');
        // [THEN] The serialized text contains the data
        Assert.IsTrue(result.Contains('href="style.css"'),
            'WriteTo must serialize the PI data into the output text');
    end;

    [Test]
    procedure WriteTo_HasProcessingInstructionSyntax()
    var
        result: Text;
    begin
        // [GIVEN/WHEN] A PI serialized to text
        result := Src.WriteToText('mytarget', 'mydata');
        // [THEN] The output starts with '<?' (PI syntax)
        Assert.IsTrue(result.Contains('<?'),
            'WriteTo output must use processing instruction syntax starting with <?');
    end;

    // ── SelectNodes ──────────────────────────────────────────────────────────────
    // PI is attached to a document before XPath queries (required for consistent
    // behaviour across BC versions). XPath 'child::*' selects children of PI —
    // PI has no children, so the result must be empty / false.

    [Test]
    procedure SelectNodes_ReturnsFalse_ForPI()
    begin
        // [GIVEN] A PI in a document with no children
        // [WHEN] SelectNodes is called with a child-selecting XPath
        // [THEN] Returns false — PI has no children to select
        Assert.IsFalse(Src.SelectNodesReturns('target', 'data', 'child::*'),
            'SelectNodes on a PI must return false (PI has no children)');
    end;

    [Test]
    procedure SelectNodes_EmptyList_ForPI()
    begin
        // [GIVEN] A PI in a document
        // [WHEN] SelectNodes is called with a child-selecting XPath
        // [THEN] NodeList is empty (count = 0)
        Assert.AreEqual(0, Src.SelectNodesCount('target', 'data', 'child::*'),
            'SelectNodes on a PI must return an empty node list (no children)');
    end;

    // ── SelectSingleNode ─────────────────────────────────────────────────────────

    [Test]
    procedure SelectSingleNode_ReturnsFalse_ForPI()
    begin
        // [GIVEN] A PI in a document with no children
        // [WHEN] SelectSingleNode is called with a child-selecting XPath
        // [THEN] Returns false — no matching child node
        Assert.IsFalse(Src.SelectSingleNodeReturns('target', 'data', 'child::*'),
            'SelectSingleNode on a PI must return false (PI has no children)');
    end;
}
