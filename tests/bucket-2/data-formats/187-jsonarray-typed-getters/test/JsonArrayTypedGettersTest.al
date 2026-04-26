codeunit 60131 "JATG Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JATG Src";

    [Test]
    procedure Get_Boolean_AtIndex0()
    begin
        Assert.IsTrue(Src.GetBooleanViaToken(Src.BuildMixedArray(), 0),
            'Element at index 0 must be a true Boolean');
    end;

    [Test]
    procedure Get_Integer_AtIndex1()
    begin
        Assert.AreEqual(42, Src.GetIntViaToken(Src.BuildMixedArray(), 1),
            'Element at index 1 must be integer 42');
    end;

    [Test]
    procedure Get_Text_AtIndex2()
    begin
        Assert.AreEqual('hello', Src.GetTextViaToken(Src.BuildMixedArray(), 2),
            'Element at index 2 must be text "hello"');
    end;

    [Test]
    procedure Get_Decimal_AtIndex3()
    begin
        Assert.AreEqual(3.5, Src.GetDecimalViaToken(Src.BuildMixedArray(), 3),
            'Element at index 3 must be decimal 3.5');
    end;

    [Test]
    procedure Get_Object_AtIndex4()
    begin
        Assert.AreEqual('v', Src.GetNestedObjectKey(Src.BuildMixedArray(), 4, 'k'),
            'Element at index 4 must be an object whose "k" key is "v"');
    end;

    [Test]
    procedure Get_Array_AtIndex5()
    begin
        Assert.AreEqual(1, Src.GetNestedArrayCount(Src.BuildMixedArray(), 5),
            'Element at index 5 must be a single-item array');
    end;

    [Test]
    procedure Get_OutOfRange_ReturnsFalse()
    begin
        // Negative case: AL's Get returns Boolean — false for out-of-range.
        Assert.IsFalse(Src.GetOutOfBoundsReturnsFalse(Src.BuildMixedArray(), 99),
            'Get with out-of-range index must return false');
    end;

    [Test]
    procedure Get_InRange_ReturnsTrue()
    begin
        Assert.IsTrue(Src.GetInRangeReturnsTrue(Src.BuildMixedArray(), 0),
            'Get with a valid index must return true');
    end;

    [Test]
    procedure Get_OutOfRange_DoesNotYieldInRangeValue()
    begin
        // Negative trap: if out-of-range Get were treated as "first element"
        // it would return true. Prove it actually returns false.
        Assert.AreNotEqual(
            Src.GetInRangeReturnsTrue(Src.BuildMixedArray(), 0),
            Src.GetOutOfBoundsReturnsFalse(Src.BuildMixedArray(), 99),
            'In-range and out-of-range Get must return different Booleans');
    end;
}
