codeunit 60002 "Dep DLL Test"
{
    Subtype = Test;

    [Test]
    procedure DepHelper_AddNumbers_ReturnsSum()
    var
        Helper: Codeunit "Dep Helper";
        Assert: Codeunit "Library Assert";
        Result: Integer;
    begin
        // [GIVEN] A dependency codeunit compiled separately and loaded via --dep-dlls
        // [WHEN] Call AddNumbers
        Result := Helper.AddNumbers(3, 4);
        // [THEN] Returns the actual sum (not the auto-stub default of 0)
        Assert.AreEqual(7, Result, 'Dep codeunit should return actual computed value, not stub default');
    end;

    [Test]
    procedure DepHelper_GetGreeting_ReturnsFormattedText()
    var
        Helper: Codeunit "Dep Helper";
        Assert: Codeunit "Library Assert";
        Result: Text;
    begin
        // [WHEN] Call GetGreeting
        Result := Helper.GetGreeting('World');
        // [THEN] Returns formatted greeting
        Assert.AreEqual('Hello, World!', Result, 'Dep codeunit should return formatted text');
    end;
}
