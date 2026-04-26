codeunit 60311 "XEX Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XEX Src";

    [Test]
    procedure GetChildNodes_Count()
    begin
        Assert.AreEqual(2, Src.GetChildNodeCount(),
            'GetChildNodes.Count must return 2 after adding two child elements');
    end;

    [Test]
    procedure GetParent_True_WhenParented()
    begin
        Assert.IsTrue(Src.GetParent_Returns_True(),
            'GetParent must return true for a child element that was Add''d');
    end;

    [Test]
    procedure AddFirst_PlacesAtFront()
    begin
        Assert.AreEqual('first', Src.AddFirst_BecomesFirstChild(),
            'AddFirst must place the new child before existing children');
    end;

    [Test]
    procedure Remove_DecreasesCount()
    begin
        Assert.AreEqual(0, Src.Remove_DecreasesChildCount(),
            'Remove must remove the child so GetChildNodes.Count is 0');
    end;

    [Test]
    procedure RemoveNodes_ClearsAllChildren()
    begin
        Assert.AreEqual(0, Src.RemoveNodes_ClearsAll(),
            'RemoveNodes must clear every child node');
    end;

    [Test]
    procedure WriteTo_ContainsElementAndAttribute()
    begin
        Assert.IsTrue(Src.WriteTo_ContainsElementName(),
            'WriteTo must serialise the element name and attributes to XML text');
    end;
}
