codeunit 1282002 "Inv Ext Overload Test"
{
    Subtype = Test;

    [Test]
    procedure PageExtInvoke_ReturnsCorrectValue()
    var
        Caller: Codeunit "Inv Ext Caller";
        Result: Integer;
    begin
        // [SCENARIO] Calling a page extension method via a Page variable
        // triggers the 3-arg Invoke(extensionId, memberId, args) on MockFormHandle.
        // Without the 3-arg overload this fails with CS1501.

        // [WHEN] We call a page extension method through the codeunit wrapper
        Result := Caller.CallExtMethod(14);

        // [THEN] The extension method should execute correctly
        Assert.AreEqual(42, Result, 'GetExtNumber(14) should return 42');
    end;

    [Test]
    procedure PageBaseInvoke_StillWorks()
    var
        Caller: Codeunit "Inv Ext Caller";
        Result: Integer;
    begin
        // [SCENARIO] Base page method call still works when page has extensions.

        // [WHEN] We call a base page method
        Result := Caller.CallBaseMethod();

        // [THEN] The 2-arg Invoke path is unaffected
        Assert.AreEqual(100, Result, 'GetBaseNumber() should return 100');
    end;

    var
        Assert: Codeunit "Library Assert";
}
