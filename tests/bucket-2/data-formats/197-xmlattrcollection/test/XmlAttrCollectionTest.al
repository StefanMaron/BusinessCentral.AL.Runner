codeunit 60241 "XAC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XAC Src";

    [Test]
    procedure Get_Existing_ReturnsValue()
    begin
        Assert.AreEqual('1', Src.GetAttrValue('id'),
            'Get(id) must return the attribute value');
    end;

    [Test]
    procedure Get_Second_Attribute()
    begin
        Assert.AreEqual('test', Src.GetAttrValue('name'),
            'Get(name) must return the second attribute value');
    end;

    [Test]
    procedure Get_Missing_ReturnsFalse()
    begin
        Assert.IsFalse(Src.GetMissing_ReturnsFalse(),
            'Get on a missing attribute must return false');
    end;

    [Test]
    procedure Set_ReplacesExistingValue()
    begin
        Assert.AreEqual('red', Src.SetAttrUpdatesValue(),
            'Set must replace an existing attribute value');
    end;

    [Test]
    procedure Set_AddsNewAttribute()
    begin
        Assert.IsTrue(Src.SetAttrAddsNew(),
            'Set must add a new attribute when none exists with that name');
    end;

    [Test]
    procedure Remove_DeletesAttribute()
    begin
        Assert.IsFalse(Src.RemoveAttr(),
            'Remove must delete the named attribute so Get returns false');
    end;

    [Test]
    procedure RemoveAll_ClearsAllAttributes()
    begin
        Assert.IsFalse(Src.RemoveAll_ClearsAll(),
            'RemoveAll must clear every attribute');
    end;

    [Test]
    procedure Count_ReflectsTwoAttributes()
    begin
        Assert.AreEqual(2, Src.Count_AfterTwoSets(),
            'Attributes().Count must return 2 after two SetAttribute calls');
    end;

    [Test]
    procedure Get_And_Remove_DifferentOutcome_NegativeTrap()
    begin
        // Negative trap: Get finds "id" but Remove deletes it.
        Assert.AreNotEqual(Src.GetMissing_ReturnsFalse(), true,
            'Get on missing must not return true');
    end;
}
