codeunit 56671 VersionHelperTest
{
    Subtype = Test;

    var
        Helper: Codeunit VersionHelper;
        Assert: Codeunit Assert;

    [Test]
    procedure TestFormatVersionPositive()
    var
        Result: Text;
    begin
        // [GIVEN] Version components 1, 2, 3, 4
        // [WHEN] Formatting via StrSubstNo with integer arguments
        Result := Helper.FormatVersion(1, 2, 3, 4);

        // [THEN] The result should be '1.2.3.4'
        Assert.AreEqual('1.2.3.4', Result, 'Expected 1.2.3.4');
    end;

    [Test]
    procedure TestFormatVersionZeros()
    var
        Result: Text;
    begin
        // [GIVEN] All-zero version components
        // [WHEN] Formatting via StrSubstNo
        Result := Helper.FormatVersion(0, 0, 0, 0);

        // [THEN] The result should be '0.0.0.0'
        Assert.AreEqual('0.0.0.0', Result, 'Expected 0.0.0.0');
    end;

    [Test]
    procedure TestFormatMessagePositive()
    var
        Result: Text;
    begin
        // [GIVEN] A template and an integer value
        // [WHEN] Calling StrSubstNo with a single integer substitution
        Result := Helper.FormatMessage('Count: %1', 42);

        // [THEN] The integer should be formatted without NullRef crash
        Assert.AreEqual('Count: 42', Result, 'Expected Count: 42');
    end;

    [Test]
    procedure TestFormatMessageNegative()
    var
        Result: Text;
    begin
        // [GIVEN] A template that expects a substitution
        // [WHEN] Calling StrSubstNo with value -99
        Result := Helper.FormatMessage('Value=%1', -99);

        // [THEN] Negative integers format correctly
        Assert.AreEqual('Value=-99', Result, 'Expected Value=-99');
    end;
}
