codeunit 305008 "IfcArr Factory Test"
{
    Subtype = Test;

    [Test]
    procedure IfcArray_LocalVar_DispatchesCorrectly()
    var
        Src: Codeunit "IfcArr Factory Src";
        Result: Integer;
    begin
        // [SCENARIO] Local `array[N] of Interface I` (BC emits MockArray<MockInterfaceHandle>(new MockInterfaceHandle.Factory(this), N))
        // [GIVEN] Items[2] := ImplB (returns 23)
        // [WHEN] reading Items[2].GetValue() through the local array
        // [THEN] dispatches to ImplB, returns 23 — proves array compiles AND carries the assigned impl (a stub returning default(int) would fail)
        Result := Src.GetValueFromLocalArray();
        Assert.AreEqual(23, Result, 'Items[2].GetValue() should dispatch to ImplB and return 23');
    end;

    [Test]
    procedure IfcArray_VarParam_DispatchesCorrectly()
    var
        Src: Codeunit "IfcArr Factory Src";
        Items: array[2] of Interface "IfcArr Factory Item";
    begin
        // [SCENARIO] var-param `array[N] of Interface I` round-trip — caller declares the array, callee fills it.
        // [GIVEN] empty local array of Interface
        // [WHEN] passing it as `var` to FillItems which assigns ImplA / ImplB
        // [THEN] both slots dispatch to the right impl with the right specific return value
        Src.FillItems(Items);
        Assert.AreEqual(17, Items[1].GetValue(), 'Items[1] should dispatch to ImplA and return 17');
        Assert.AreEqual(23, Items[2].GetValue(), 'Items[2] should dispatch to ImplB and return 23');
    end;

    var
        Assert: Codeunit "Library Assert";
}
