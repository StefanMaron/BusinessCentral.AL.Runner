// Tests for issue #1528: user-defined ToText() method must not be rewritten
// by the Roslyn rewriter's `.ToText() → AlCompat.Format()` rule.

codeunit 1319002 "ToText User Method Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";
        Src: Codeunit "ToText User Method Src";

    /// Positive: calling the user-defined ToText() directly must return the
    /// correct string value, not a default or garbage result.
    [Test]
    procedure UserToText_DirectCall_ReturnsExpectedValue()
    begin
        Assert.AreEqual('hello from user ToText', Src.ToText(), 'direct ToText() must return the method body result');
    end;

    /// Positive: calling ToText() from within the codeunit (base.Parent.ToText())
    /// must also return the correct value — this is the exact pattern that triggered
    /// CS0029 before the fix (rewriter replaced the call with AlCompat.Format).
    [Test]
    procedure UserToText_CalledFromSameCU_ReturnsExpectedValue()
    begin
        Assert.AreEqual('hello from user ToText', Src.CallsOwnToText(), 'internal ToText() call must return the method body result');
    end;

    /// Negative: if ToText() were rewritten to AlCompat.Format(codeunitInstance),
    /// the result would be a garbage string like a type name — NOT the user-defined
    /// body content. Verify the value is NOT empty and is exactly the defined literal.
    [Test]
    procedure UserToText_IsNotEmpty_AndNotTypeNameGarbage()
    var
        result: Text;
    begin
        result := Src.ToText();
        Assert.AreNotEqual('', result, 'ToText() must not return empty (AlCompat.Format on codeunit would return type name)');
        Assert.AreEqual('hello from user ToText', result, 'must be the exact literal from the method body');
    end;

    /// Smoke: BC runtime Format() wrapping (the actual AlCompat.Format path)
    /// must still work for Variant → Text conversion, unaffected by the fix.
    [Test]
    procedure FormatVariant_StillWorks_AfterFix()
    var
        result: Text;
        v: Integer;
    begin
        v := 42;
        result := Src.FormatValue(v);
        Assert.AreEqual('42', result, 'Format(Variant) must still produce the formatted string');
    end;
}
