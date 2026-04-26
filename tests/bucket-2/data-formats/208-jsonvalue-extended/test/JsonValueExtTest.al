codeunit 60351 "JVX Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JVX Src";

    [Test]
    procedure SetValue_AsInteger_RoundTrip()
    begin
        Assert.AreEqual(42, Src.SetAndGetInteger(),
            'SetValue(42) + AsInteger must return 42');
    end;

    [Test]
    procedure SetValue_AsText_RoundTrip()
    begin
        Assert.AreEqual('hello', Src.SetAndGetText(),
            'SetValue(hello) + AsText must return hello');
    end;

    [Test]
    procedure SetValue_AsBoolean_RoundTrip()
    begin
        Assert.IsTrue(Src.SetAndGetBoolean(),
            'SetValue(true) + AsBoolean must return true');
    end;

    [Test]
    procedure SetValue_AsDecimal_RoundTrip()
    begin
        Assert.AreEqual(3.14, Src.SetAndGetDecimal(),
            'SetValue(3.14) + AsDecimal must return 3.14');
    end;

    [Test]
    procedure IsUndefined_False_ForDefault()
    begin
        // BC's default-initialised JsonValue is not "undefined" — it holds a null token.
        Assert.IsFalse(Src.IsUndefined_Default(),
            'A default JsonValue.IsUndefined returns false (null != undefined)');
    end;

    [Test]
    procedure IsUndefined_False_AfterSet()
    begin
        Assert.IsFalse(Src.IsUndefined_AfterSet(),
            'JsonValue.IsUndefined must be false after SetValue');
    end;

    [Test]
    procedure Path_NestedValue()
    begin
        // JSONPath notation: "$.score" for a value under key "score".
        Assert.AreEqual('$.score', Src.PathOfNestedValue(),
            'Path of a value under key "score" must be "$.score"');
    end;

    [Test]
    procedure AsToken_RoundTrip()
    begin
        Assert.AreEqual(77, Src.AsTokenRoundTrip(),
            'AsToken().AsValue().AsInteger must round-trip the value');
    end;

    [Test]
    procedure Clone_IsIndependent()
    begin
        Assert.IsTrue(Src.CloneIsIndependent(),
            'Clone must produce an independent copy');
    end;

    [Test]
    procedure SetValue_Changes_Value_NegativeTrap()
    begin
        // Negative trap: SetValue must actually change the stored value.
        Assert.AreNotEqual(0, Src.SetAndGetInteger(),
            'SetValue must not leave the value at default 0');
    end;
}
