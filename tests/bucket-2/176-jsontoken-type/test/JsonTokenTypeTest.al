codeunit 97101 "JTT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // --- IsValue ---

    [Test]
    procedure IsValue_IntegerToken_ReturnsTrue()
    var
        Src: Codeunit "JTT Src";
        JArr: JsonArray;
        JT: JsonToken;
    begin
        JArr.Add(7);
        JArr.Get(0, JT);
        Assert.AreEqual('value', Src.TokenKind(JT), 'Integer token should report IsValue=true');
    end;

    [Test]
    procedure IsValue_TextToken_ReturnsTrue()
    var
        Src: Codeunit "JTT Src";
        JArr: JsonArray;
        JT: JsonToken;
    begin
        JArr.Add('hello');
        JArr.Get(0, JT);
        Assert.AreEqual('value', Src.TokenKind(JT), 'Text token should report IsValue=true');
    end;

    // --- IsArray ---

    [Test]
    procedure IsArray_ArrayToken_ReturnsTrue()
    var
        Src: Codeunit "JTT Src";
        JObj: JsonObject;
        Inner: JsonArray;
        JT: JsonToken;
    begin
        Inner.Add(1);
        JObj.Add('items', Inner);
        JObj.Get('items', JT);
        Assert.AreEqual('array', Src.TokenKind(JT), 'Array token should report IsArray=true');
    end;

    // --- IsObject ---

    [Test]
    procedure IsObject_ObjectToken_ReturnsTrue()
    var
        Src: Codeunit "JTT Src";
        JObj: JsonObject;
        Inner: JsonObject;
        JT: JsonToken;
    begin
        Inner.Add('k', 'v');
        JObj.Add('nested', Inner);
        JObj.Get('nested', JT);
        Assert.AreEqual('object', Src.TokenKind(JT), 'Object token should report IsObject=true');
    end;

    // --- Negative: branches are distinct ---

    [Test]
    procedure TokenKind_NotSameForAllTypes()
    var
        Src: Codeunit "JTT Src";
        JObj: JsonObject;
        Inner: JsonArray;
        JT: JsonToken;
        JArr: JsonArray;
        JT2: JsonToken;
    begin
        // array token
        Inner.Add(1);
        JObj.Add('arr', Inner);
        JObj.Get('arr', JT);
        // value token
        JArr.Add(42);
        JArr.Get(0, JT2);
        Assert.AreNotEqual(Src.TokenKind(JT), Src.TokenKind(JT2), 'Array and value tokens must report different kinds');
    end;

    // --- AsValue ---

    [Test]
    procedure AsValue_IntegerToken_ReturnsCorrectInt()
    var
        Src: Codeunit "JTT Src";
        JArr: JsonArray;
        JT: JsonToken;
    begin
        JArr.Add(42);
        JArr.Get(0, JT);
        Assert.AreEqual(42, Src.ExtractInt(JT), 'AsValue().AsInteger() must return 42');
    end;

    [Test]
    procedure AsValue_TextToken_ReturnsCorrectText()
    var
        Src: Codeunit "JTT Src";
        JArr: JsonArray;
        JT: JsonToken;
    begin
        JArr.Add('runner');
        JArr.Get(0, JT);
        Assert.AreEqual('runner', Src.ExtractText(JT), 'AsValue().AsText() must return ''runner''');
    end;

    // --- AsArray ---

    [Test]
    procedure AsArray_ArrayToken_ReturnsCountableArray()
    var
        Src: Codeunit "JTT Src";
        JObj: JsonObject;
        Inner: JsonArray;
        JT: JsonToken;
    begin
        Inner.Add('x');
        Inner.Add('y');
        JObj.Add('list', Inner);
        JObj.Get('list', JT);
        Assert.AreEqual(2, Src.ArrayCount(JT), 'AsArray().Count() must return 2');
    end;

    // --- AsObject ---

    [Test]
    procedure AsObject_ObjectToken_ContainsExpectedKey()
    var
        Src: Codeunit "JTT Src";
        JObj: JsonObject;
        Inner: JsonObject;
        JT: JsonToken;
    begin
        Inner.Add('city', 'Berlin');
        JObj.Add('addr', Inner);
        JObj.Get('addr', JT);
        Assert.IsTrue(Src.ObjectContains(JT, 'city'), 'AsObject() must contain key ''city''');
    end;

    // --- Clone ---

    [Test]
    procedure Clone_ValueToken_CloneHasSameText()
    var
        Src: Codeunit "JTT Src";
        JArr: JsonArray;
        JT: JsonToken;
    begin
        JArr.Add('original');
        JArr.Get(0, JT);
        Assert.AreEqual('original', Src.CloneAndRead(JT), 'Clone().AsValue().AsText() must return ''original''');
    end;
}
