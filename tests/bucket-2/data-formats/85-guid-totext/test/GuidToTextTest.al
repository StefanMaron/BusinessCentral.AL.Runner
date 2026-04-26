codeunit 82101 "GT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "GT Src";

    // -----------------------------------------------------------------------
    // Positive: default ToText() — 38 chars with braces and hyphens
    // -----------------------------------------------------------------------

    [Test]
    procedure ToText_Default_Returns38Chars()
    var
        g: Guid;
    begin
        // Positive: g.ToText() must return exactly 38 characters
        g := CreateGuid();
        Assert.AreEqual(38, Src.DefaultLength(g),
            'Guid.ToText() must return a 38-character string');
    end;

    [Test]
    procedure ToText_Default_StartsWithBrace()
    var
        g: Guid;
        Result: Text;
    begin
        // Positive: g.ToText() result starts with '{'
        g := CreateGuid();
        Result := Src.ToTextDefault(g);
        Assert.AreEqual('{', Result[1],
            'Guid.ToText() result must start with {');
    end;

    [Test]
    procedure ToText_Default_EndsWithBrace()
    var
        g: Guid;
        Result: Text;
    begin
        // Positive: g.ToText() result ends with '}'
        g := CreateGuid();
        Result := Src.ToTextDefault(g);
        Assert.AreEqual('}', Result[StrLen(Result)],
            'Guid.ToText() result must end with }');
    end;

    [Test]
    procedure ToText_ExplicitTrue_Returns38Chars()
    var
        g: Guid;
    begin
        // Positive: g.ToText(true) must also return 38 characters
        g := CreateGuid();
        Assert.AreEqual(38, StrLen(Src.ToTextWithDelimiters(g)),
            'Guid.ToText(true) must return a 38-character string');
    end;

    [Test]
    procedure ToText_DefaultAndExplicitTrue_AreEqual()
    var
        g: Guid;
    begin
        // Positive: ToText() and ToText(true) must return the same value
        g := CreateGuid();
        Assert.AreEqual(Src.ToTextDefault(g), Src.ToTextWithDelimiters(g),
            'Guid.ToText() and Guid.ToText(true) must return the same string');
    end;

    // -----------------------------------------------------------------------
    // Positive: ToText(false) — 32 chars without delimiters
    // -----------------------------------------------------------------------

    [Test]
    procedure ToText_False_Returns32Chars()
    var
        g: Guid;
    begin
        // Positive: g.ToText(false) must return exactly 32 characters
        g := CreateGuid();
        Assert.AreEqual(32, Src.NoDelimLength(g),
            'Guid.ToText(false) must return a 32-character string');
    end;

    [Test]
    procedure ToText_False_NoHyphens()
    var
        g: Guid;
        Result: Text;
    begin
        // Positive: g.ToText(false) result has no hyphens
        g := CreateGuid();
        Result := Src.ToTextNoDelimiters(g);
        Assert.AreEqual(0, StrPos(Result, '-'),
            'Guid.ToText(false) result must contain no hyphens');
    end;

    // -----------------------------------------------------------------------
    // Negative: ToText() and ToText(false) differ in length
    // -----------------------------------------------------------------------

    [Test]
    procedure ToText_Default_LongerThanNoDelim()
    var
        g: Guid;
    begin
        // Negative: default format must be longer than no-delimiter format
        g := CreateGuid();
        Assert.IsTrue(Src.DefaultLength(g) > Src.NoDelimLength(g),
            'Guid.ToText() must be longer than Guid.ToText(false)');
    end;

    [Test]
    procedure ToText_Default_NotEqualToNoDelim()
    var
        g: Guid;
    begin
        // Negative: default and no-delimiter results must differ
        g := CreateGuid();
        Assert.AreNotEqual(Src.ToTextDefault(g), Src.ToTextNoDelimiters(g),
            'Guid.ToText() and Guid.ToText(false) must return different strings');
    end;
}
