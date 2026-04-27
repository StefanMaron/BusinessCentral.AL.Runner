codeunit 1314002 "Char Evaluate Repro Tests"
{
    Subtype = Test;

    var
        Repro: Codeunit "Char Evaluate Repro";
        Assert: Codeunit Assert;

    [Test]
    procedure EvaluateChar_SingleChar_ReturnsTrue()
    var
        Result: Char;
        Ok: Boolean;
    begin
        // [SCENARIO] Evaluate a single-character text into a Char variable returns true
        // and assigns the correct character value
        Ok := Repro.TryParseChar('A', Result);
        Assert.IsTrue(Ok, 'Evaluate(Char, ''A'') should return true');
        Assert.AreEqual('A', Format(Result), 'Evaluate(Char, ''A'') should yield A');
    end;

    [Test]
    procedure EvaluateChar_NumericChar_ReturnsTrue()
    var
        Result: Char;
        Ok: Boolean;
    begin
        // [SCENARIO] Evaluate a single digit character succeeds
        Ok := Repro.TryParseChar('5', Result);
        Assert.IsTrue(Ok, 'Evaluate(Char, ''5'') should return true');
        Assert.AreEqual('5', Format(Result), 'Evaluate(Char, ''5'') should yield 5');
    end;

    [Test]
    procedure EvaluateChar_MultiCharText_ReturnsFalse()
    var
        Result: Char;
        Ok: Boolean;
    begin
        // [SCENARIO] Evaluate a multi-character text returns false
        Ok := Repro.TryParseChar('multi', Result);
        Assert.IsFalse(Ok, 'Evaluate(Char, ''multi'') should return false');
    end;

    [Test]
    procedure EvaluateChar_EmptyText_ReturnsFalse()
    var
        Result: Char;
        Ok: Boolean;
    begin
        // [SCENARIO] Evaluate an empty text returns false
        Ok := Repro.TryParseChar('', Result);
        Assert.IsFalse(Ok, 'Evaluate(Char, '''') should return false');
    end;
}
