codeunit 134002 "AMD Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Src: Codeunit "AMD Source";

    [Test]
    procedure TwoDimArray_Get_ReturnsCorrectElement()
    begin
        // [GIVEN] 2×3 array with distinct values
        // [THEN] 2-arg indexer returns correct element
        Assert.AreEqual(11, Src.Get2DElement(1, 1), 'arr[1,1] must be 11');
        Assert.AreEqual(12, Src.Get2DElement(1, 2), 'arr[1,2] must be 12');
        Assert.AreEqual(23, Src.Get2DElement(2, 3), 'arr[2,3] must be 23');
    end;

    [Test]
    procedure TwoDimArray_Set_RetainsValue()
    begin
        // [GIVEN] value assigned via arr[2,3] := 99
        // [THEN] same 2-arg indexer reads back 99
        Assert.AreEqual(99, Src.Set2DElement(), 'arr[2,3] after assignment must be 99');
    end;

    [Test]
    procedure GetSubArray_FirstRow_SumsCorrectly()
    begin
        // [GIVEN] arr[1] = {10, 20, 30}; passed to SumRow via GetSubArray(0)
        // [THEN] Sum = 60
        Assert.AreEqual(60, Src.SumFirstRow(), 'Sum of first row must be 60');
    end;

    [Test]
    procedure GetSubArray_SecondRow_SumsCorrectly()
    begin
        // [GIVEN] arr[2] = {40, 50, 60}; passed to SumRow via GetSubArray(1)
        // [THEN] Sum = 150
        Assert.AreEqual(150, Src.SumSecondRow(), 'Sum of second row must be 150');
    end;
}
