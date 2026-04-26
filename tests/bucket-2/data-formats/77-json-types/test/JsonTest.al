codeunit 56900 "Json Types Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestJsonObjectAddAndGet()
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('name', 'Alice');
        Assert.IsTrue(JObj.Get('name', JTok), 'Get should return true for existing key');
        Assert.AreEqual('Alice', JTok.AsValue().AsText(), 'Value mismatch');
    end;

    [Test]
    procedure TestJsonObjectGetNonExistent()
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        Assert.IsFalse(JObj.Get('missing', JTok), 'Get should return false for missing key');
    end;

    [Test]
    procedure TestJsonObjectMultipleKeys()
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('a', 1);
        JObj.Add('b', 2);
        JObj.Get('a', JTok);
        Assert.AreEqual(1, JTok.AsValue().AsInteger(), 'Integer value a');
        JObj.Get('b', JTok);
        Assert.AreEqual(2, JTok.AsValue().AsInteger(), 'Integer value b');
    end;

    [Test]
    procedure TestJsonArrayAddAndCount()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        i: Integer;
    begin
        JArr.Add('first');
        JArr.Add('second');
        JArr.Add('third');
        Assert.AreEqual(3, JArr.Count(), 'Array should have 3 elements');
        Assert.IsTrue(JArr.Get(0, JTok), 'Get index 0 should succeed');
        Assert.AreEqual('first', JTok.AsValue().AsText(), 'First element');
    end;

    [Test]
    procedure TestJsonArrayGetOutOfRange()
    var
        JArr: JsonArray;
        JTok: JsonToken;
    begin
        JArr.Add('only');
        Assert.IsFalse(JArr.Get(5, JTok), 'Get out-of-range index should return false');
    end;

    [Test]
    procedure TestJsonValueText()
    var
        JVal: JsonValue;
    begin
        JVal.SetValue('hello');
        Assert.AreEqual('hello', JVal.AsText(), 'Text value');
    end;

    [Test]
    procedure TestJsonValueInteger()
    var
        JVal: JsonValue;
    begin
        JVal.SetValue(42);
        Assert.AreEqual(42, JVal.AsInteger(), 'Integer value');
    end;

    [Test]
    procedure TestJsonValueBoolean()
    var
        JVal: JsonValue;
    begin
        JVal.SetValue(true);
        Assert.IsTrue(JVal.AsBoolean(), 'Boolean value should be true');
    end;

    [Test]
    procedure TestJsonObjectReplace()
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('key', 'old');
        JObj.Replace('key', 'new');
        JObj.Get('key', JTok);
        Assert.AreEqual('new', JTok.AsValue().AsText(), 'Replaced value');
    end;

    [Test]
    procedure TestJsonObjectRemove()
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('temp', 'data');
        Assert.IsTrue(JObj.Remove('temp'), 'Remove should return true');
        Assert.IsFalse(JObj.Get('temp', JTok), 'Get after remove should return false');
    end;

    [Test]
    procedure TestJsonObjectContains()
    var
        JObj: JsonObject;
    begin
        JObj.Add('exists', 'yes');
        Assert.IsTrue(JObj.Contains('exists'), 'Contains should return true');
        Assert.IsFalse(JObj.Contains('nope'), 'Contains should return false for missing');
    end;

    [Test]
    procedure TestJsonWriteReadText()
    var
        JObj: JsonObject;
        JObj2: JsonObject;
        JTok: JsonToken;
        JsonText: Text;
    begin
        JObj.Add('round', 'trip');
        JObj.WriteTo(JsonText);
        Assert.IsTrue(JObj2.ReadFrom(JsonText), 'ReadFrom should succeed');
        JObj2.Get('round', JTok);
        Assert.AreEqual('trip', JTok.AsValue().AsText(), 'Round-trip value');
    end;

    [Test]
    procedure TestJsonReadFromInvalid()
    var
        JObj: JsonObject;
    begin
        Assert.IsFalse(JObj.ReadFrom('not valid json{{{'), 'ReadFrom should return false for invalid JSON');
    end;

    [Test]
    procedure TestJsonSelectToken()
    var
        JObj: JsonObject;
        Inner: JsonObject;
        JTok: JsonToken;
    begin
        Inner.Add('city', 'Berlin');
        JObj.Add('address', Inner);
        Assert.IsTrue(JObj.SelectToken('address.city', JTok), 'SelectToken should find nested key');
        Assert.AreEqual('Berlin', JTok.AsValue().AsText(), 'SelectToken value');
    end;

    [Test]
    procedure TestJsonSelectTokenMissing()
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('x', 1);
        Assert.IsFalse(JObj.SelectToken('nonexistent.path', JTok), 'SelectToken should return false for missing path');
    end;
}
