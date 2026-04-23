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

    [Test]
    procedure TestAppendLineEmitsLfOnly_NotCrlf()
    var
        TB: TextBuilder;
        Result: Text;
        LF: Char;
        CR: Char;
    begin
        // [SCENARIO] BC's TextBuilder.AppendLine must emit bare LF on every OS,
        //            regardless of host line-ending conventions.
        LF := 10;
        CR := 13;

        // [WHEN] AppendLine is called
        TB.AppendLine('line1');
        TB.Append('line2');
        Result := TB.ToText();

        // [THEN] Output contains LF (positive)
        Assert.IsTrue(StrPos(Result, Format(LF)) > 0, 'Output must contain LF (Char(10))');

        // [THEN] Output contains NO CR (negative — guards against CRLF leaking from host OS)
        Assert.AreEqual(0, StrPos(Result, Format(CR)), 'Output must not contain CR (Char(13))');
    end;
}
