codeunit 59721 "RAA Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RAA Src";

    [Test]
    procedure RemoveAllAttrs_Baseline_ThreeAttrs()
    var
        elt: XmlElement;
    begin
        // Baseline: BuildWithThreeAttrs produces an element with 3 attributes.
        // This catches a no-op Build that would otherwise make the RemoveAll
        // test trivially pass with zero inputs.
        elt := Src.BuildWithThreeAttrs();
        Assert.AreEqual(3, Src.AttrCount(elt),
            'BuildWithThreeAttrs must produce 3 attributes');
    end;

    [Test]
    procedure RemoveAllAttrs_ClearsAll()
    begin
        // Positive: after RemoveAttributes, attribute count is 0.
        Assert.AreEqual(0, Src.RoundTripCount(),
            'After RemoveAttributes, attribute count must be 0');
    end;

    [Test]
    procedure RemoveAllAttrs_PreservesChildren()
    begin
        // Positive: children survive attribute removal — child count must be 1
        // (element had one <child/> before RemoveAttributes).
        Assert.AreEqual(1, Src.PreservesChildren(),
            'RemoveAttributes must not remove child elements');
    end;

    [Test]
    procedure RemoveAllAttrs_PreservesName()
    begin
        // Positive: the element's LocalName is unaffected by attribute removal.
        Assert.AreEqual('root', Src.PreservesName(),
            'RemoveAttributes must not change the element name');
    end;

    [Test]
    procedure RemoveAllAttrs_Idempotent()
    var
        elt: XmlElement;
    begin
        // Calling RemoveAttributes twice in a row must not throw and must still
        // leave the element with zero attributes.
        elt := Src.BuildWithThreeAttrs();
        Src.RemoveAll(elt);
        Src.RemoveAll(elt);
        Assert.AreEqual(0, Src.AttrCount(elt),
            'Double-RemoveAttributes must still yield 0 attributes');
    end;

    [Test]
    procedure RemoveAllAttrs_OnEmptyAttrElement()
    var
        elt: XmlElement;
    begin
        // Edge: calling RemoveAttributes on an element with no attributes is a no-op.
        elt := XmlElement.Create('bare');
        Src.RemoveAll(elt);
        Assert.AreEqual(0, Src.AttrCount(elt),
            'RemoveAttributes on an element with no attributes must not throw');
    end;

    [Test]
    procedure RemoveAllAttrs_DoesNotAffectOtherElement_NegativeTrap()
    var
        a: XmlElement;
        b: XmlElement;
    begin
        // Negative: RemoveAttributes must only affect the receiver. Removing
        // from element a must leave element b's attributes intact.
        a := Src.BuildWithThreeAttrs();
        b := Src.BuildWithThreeAttrs();
        Src.RemoveAll(a);
        Assert.AreEqual(3, Src.AttrCount(b),
            'RemoveAttributes on element a must not touch element b');
    end;
}
