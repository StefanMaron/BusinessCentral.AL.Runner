codeunit 59691 "SSN Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SSN Src";

    [Test]
    procedure SelectSingleNode_ChildMatch_ReturnsTrue()
    begin
        // Positive: direct child `a` exists (relative to root element).
        Assert.IsTrue(Src.SelectByPath_FromElement('a'),
            'SelectSingleNode must return true when matching child "a"');
    end;

    [Test]
    procedure SelectSingleNode_NestedMatch_ReturnsTrue()
    begin
        // Positive: nested descendant `b/c` exists via relative path.
        Assert.IsTrue(Src.SelectByPath_FromElement('b/c'),
            'SelectSingleNode must return true when matching b/c');
    end;

    [Test]
    procedure SelectSingleNode_Descendant_ReturnsTrue()
    begin
        // Positive: `//c` finds the c element anywhere in the tree.
        Assert.IsTrue(Src.SelectByPath_FromElement('//c'),
            'SelectSingleNode must return true when matching //c');
    end;

    [Test]
    procedure SelectSingleNode_NoMatch_ReturnsFalse()
    begin
        // Negative: `zzz` does not exist as a direct child.
        Assert.IsFalse(Src.SelectByPath_FromElement('zzz'),
            'SelectSingleNode must return false for non-existent "zzz"');
    end;

    [Test]
    procedure SelectSingleNode_NoMatchDescendant_ReturnsFalse()
    begin
        // Negative: `//missing` does not exist anywhere.
        Assert.IsFalse(Src.SelectByPath_FromElement('//missing'),
            'SelectSingleNode must return false for non-existent //missing');
    end;

    [Test]
    procedure SelectSingleNode_ReturnsNodeWithCorrectName()
    begin
        // Proving: the selected node exposes the correct LocalName ('a' for child "a").
        Assert.AreEqual('a', Src.SelectAndGetName('a'),
            'Selected node for "a" must have LocalName "a"');
    end;

    [Test]
    procedure SelectSingleNode_NestedReturnsDeepestName()
    begin
        // Proving: b/c selects the innermost <c> element.
        Assert.AreEqual('c', Src.SelectAndGetName('b/c'),
            'Selected node for "b/c" must have LocalName "c"');
    end;

    [Test]
    procedure SelectSingleNode_DifferentPaths_DifferentNames_NegativeTrap()
    begin
        // Negative trap: guard against a stub that always returns the same node.
        Assert.AreNotEqual(
            Src.SelectAndGetName('a'),
            Src.SelectAndGetName('b'),
            'Different XPath matches must return different element names');
    end;
}
