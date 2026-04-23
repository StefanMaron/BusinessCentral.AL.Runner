codeunit 109001 "Rich Auto Stub Test"
{
    Subtype = Test;

    [Test]
    procedure CallDepMethod_ReturnsValue()
    var
        Helper: Codeunit "Rich Stub Helper";
        Assert: Codeunit "Library Assert";
        Result: Integer;
    begin
        // [GIVEN] A codeunit from the test toolkit range
        // [WHEN] Call a method on it
        Result := Helper.ComputeValue(5);
        // [THEN] Returns the computed value (10, since source is compiled here)
        Assert.AreEqual(10, Result, 'ComputeValue should return input * 2');
    end;

    [Test]
    procedure CallDepMethod_ReturnsText()
    var
        Helper: Codeunit "Rich Stub Helper";
        Assert: Codeunit "Library Assert";
        Result: Text;
    begin
        Result := Helper.FormatLabel('Item-', 42);
        Assert.AreEqual('Item-42', Result, 'FormatLabel should concatenate prefix and value');
    end;
}
