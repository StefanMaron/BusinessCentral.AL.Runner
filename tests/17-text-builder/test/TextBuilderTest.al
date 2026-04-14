codeunit 50917 "Text Builder Tests"
{
    Subtype = Test;

    var
        Helper: Codeunit "Text Builder Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestBuildGreeting()
    var
        Result: Text;
        LF: Char;
    begin
        // [WHEN] Building a greeting
        Result := Helper.BuildGreeting('Smith', 'Mr.');

        // [THEN] The result should contain the expected lines
        LF := 10;
        Assert.AreEqual(
            'Dear Mr. Smith,' + Format(LF) + 'Welcome to our service.' + Format(LF) + 'Best regards',
            Result,
            'Greeting text should match expected output');
    end;

    [Test]
    procedure TestBuildList()
    var
        Result: Text;
        LF: Char;
    begin
        // [WHEN] Building a list
        Result := Helper.BuildList('Apple', 'Banana', 'Cherry');

        // [THEN] The list should be formatted correctly
        LF := 10;
        Assert.AreEqual(
            'Items:' + Format(LF) + '- Apple' + Format(LF) + '- Banana' + Format(LF) + '- Cherry',
            Result,
            'List text should match expected output');
    end;
}
