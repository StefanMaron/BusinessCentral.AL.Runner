/// Tests for JsonArray integer-index overloads: GetText, GetInteger, GetDecimal, GetBoolean, GetArray.
/// Regression for issue #1426: CS1503 'int' → 'string' when passing Integer index to JsonArray.GetText(Integer).
codeunit 316101 "Json Array Indexed Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Json Array Indexed Src";

    // ── GetText(Integer) ──────────────────────────────────────────────────────

    [Test]
    procedure GetTextAtIndex_Positive()
    var
        Arr: JsonArray;
        Result: Text;
    begin
        // [GIVEN] A JSON array with two string elements
        Arr.Add('Hello');
        Arr.Add('World');

        // [WHEN] Getting text at index 0 (first element)
        Result := Helper.GetTextAtIndex(Arr, 0);

        // [THEN] Returns the correct string value
        Assert.AreEqual('Hello', Result, 'GetText(0) should return first element');
    end;

    [Test]
    procedure GetTextAtIndex_SecondElement()
    var
        Arr: JsonArray;
        Result: Text;
    begin
        // [GIVEN] A JSON array with two string elements
        Arr.Add('Alpha');
        Arr.Add('Beta');

        // [WHEN] Getting text at index 1 (second element)
        Result := Helper.GetTextAtIndex(Arr, 1);

        // [THEN] Returns the second element
        Assert.AreEqual('Beta', Result, 'GetText(1) should return second element');
    end;

    [Test]
    procedure GetTextAtIndex_OutOfBounds()
    var
        Arr: JsonArray;
    begin
        // [GIVEN] An empty JSON array
        // [WHEN] Getting text at an out-of-bounds index
        asserterror Helper.GetTextAtIndex(Arr, 5);

        // [THEN] An error is raised
        Assert.ExpectedError('');
    end;

    // ── GetInteger(Integer) ───────────────────────────────────────────────────

    [Test]
    procedure GetIntegerAtIndex_Positive()
    var
        Arr: JsonArray;
        Result: Integer;
    begin
        // [GIVEN] A JSON array with integer elements
        Arr.Add(10);
        Arr.Add(42);

        // [WHEN] Getting integer at index 1
        Result := Helper.GetIntegerAtIndex(Arr, 1);

        // [THEN] Returns 42 (not zero — proving the value is read correctly)
        Assert.AreEqual(42, Result, 'GetInteger(1) should return 42');
    end;

    [Test]
    procedure GetIntegerAtIndex_OutOfBounds()
    var
        Arr: JsonArray;
    begin
        asserterror Helper.GetIntegerAtIndex(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetDecimal(Integer) ───────────────────────────────────────────────────

    [Test]
    procedure GetDecimalAtIndex_Positive()
    var
        Arr: JsonArray;
        Result: Decimal;
    begin
        Arr.Add(3.14);
        Arr.Add(2.72);

        Result := Helper.GetDecimalAtIndex(Arr, 0);

        Assert.AreEqual(3.14, Result, 'GetDecimal(0) should return 3.14');
    end;

    [Test]
    procedure GetDecimalAtIndex_OutOfBounds()
    var
        Arr: JsonArray;
    begin
        asserterror Helper.GetDecimalAtIndex(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetBoolean(Integer) ───────────────────────────────────────────────────

    [Test]
    procedure GetBooleanAtIndex_Positive()
    var
        Arr: JsonArray;
        Result: Boolean;
    begin
        Arr.Add(false);
        Arr.Add(true);

        Result := Helper.GetBooleanAtIndex(Arr, 1);

        // Proves the value is read correctly (not default false)
        Assert.IsTrue(Result, 'GetBoolean(1) should return true');
    end;

    [Test]
    procedure GetBooleanAtIndex_OutOfBounds()
    var
        Arr: JsonArray;
    begin
        asserterror Helper.GetBooleanAtIndex(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetArray(Integer) ─────────────────────────────────────────────────────

    [Test]
    procedure GetArrayAtIndex_Positive()
    var
        OuterArr: JsonArray;
        InnerArr: JsonArray;
        Retrieved: JsonArray;
    begin
        // [GIVEN] A nested JSON array
        InnerArr.Add('nested');
        OuterArr.Add(InnerArr);

        // [WHEN] Getting the inner array at index 0
        Retrieved := Helper.GetArrayAtIndex(OuterArr, 0);

        // [THEN] The inner array has the expected element count
        Assert.AreEqual(1, Retrieved.Count(), 'Retrieved inner array should have 1 element');
    end;

    [Test]
    procedure GetArrayAtIndex_OutOfBounds()
    var
        Arr: JsonArray;
    begin
        asserterror Helper.GetArrayAtIndex(Arr, 0);
        Assert.ExpectedError('');
    end;
}
