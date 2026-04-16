codeunit 83801 "JV Conv Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JV Conv Src";

    [Test]
    procedure SetValue_AsText_RoundTrip()
    begin
        Assert.AreEqual('hello', Src.TextRoundTrip(), 'SetValue text then AsText must return same value');
    end;

    [Test]
    procedure SetValue_AsInteger_RoundTrip()
    begin
        Assert.AreEqual(42, Src.IntegerRoundTrip(), 'SetValue integer then AsInteger must return 42');
    end;

    [Test]
    procedure SetValue_AsBoolean_True()
    begin
        Assert.IsTrue(Src.BooleanTrueRoundTrip(), 'SetValue true then AsBoolean must return true');
    end;

    [Test]
    procedure SetValue_AsBoolean_False()
    begin
        Assert.IsFalse(Src.BooleanFalseRoundTrip(), 'SetValue false then AsBoolean must return false');
    end;

    [Test]
    procedure SetValue_AsDecimal_RoundTrip()
    var
        Expected: Decimal;
    begin
        Expected := 3.14;
        Assert.AreEqual(Expected, Src.DecimalRoundTrip(), 'SetValue decimal then AsDecimal must return 3.14');
    end;

    [Test]
    procedure SetValueToNull_IsNull_True()
    begin
        Assert.IsTrue(Src.NullIsNull(), 'SetValueToNull then IsNull must return true');
    end;

    [Test]
    procedure SetValue_IsNull_False()
    begin
        Assert.IsFalse(Src.NonNullIsNotNull(), 'SetValue text then IsNull must return false');
    end;

    [Test]
    procedure AsText_ViaJsonToken()
    begin
        Assert.AreEqual('world', Src.TextFromToken(), 'AsText via JsonArray/JsonToken must return ''world''');
    end;

    [Test]
    procedure AsInteger_ViaJsonToken()
    begin
        Assert.AreEqual(99, Src.IntegerFromToken(), 'AsInteger via JsonArray/JsonToken must return 99');
    end;

    [Test]
    procedure AsBoolean_ViaJsonToken()
    begin
        Assert.IsTrue(Src.BooleanFromToken(), 'AsBoolean via JsonArray/JsonToken must return true');
    end;

    [Test]
    procedure AsText_ViaJsonObjectGet()
    begin
        Assert.AreEqual('Alice', Src.TextFromObjectGet(), 'AsText via JsonObject.Get must return ''Alice''');
    end;

    [Test]
    procedure AsInteger_ViaJsonObjectGet()
    begin
        Assert.AreEqual(7, Src.IntegerFromObjectGet(), 'AsInteger via JsonObject.Get must return 7');
    end;

    [Test]
    procedure AsText_NotEmpty()
    var
        JV: JsonValue;
    begin
        JV.SetValue('test');
        Assert.AreNotEqual('', JV.AsText(), 'AsText on set value must not be empty');
    end;
}
