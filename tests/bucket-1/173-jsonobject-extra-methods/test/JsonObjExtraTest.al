codeunit 100001 "JOE Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JOE Src";

    [Test]
    procedure GetChar_ReturnsCharacterValue()
    var
        Obj: JsonObject;
    begin
        // Char 65 = 'A'
        Obj.Add('ch', 65);
        Assert.AreEqual('A', Src.GetCharValue(Obj, 'ch'), 'GetChar must return the character for code point 65');
    end;

    [Test]
    procedure GetChar_MissingKey_Throws()
    var
        Obj: JsonObject;
    begin
        asserterror Src.GetCharValue(Obj, 'nope');
        Assert.ExpectedError('');
    end;

    [Test]
    procedure GetDate_ReturnsDateValue()
    var
        Obj: JsonObject;
        Expected: Date;
    begin
        Expected := DMY2Date(15, 6, 2024);
        Obj.Add('dt', Expected);
        Assert.AreEqual(Expected, Src.GetDateValue(Obj, 'dt'), 'GetDate must return the stored date');
    end;

    [Test]
    procedure GetDate_MissingKey_Throws()
    var
        Obj: JsonObject;
    begin
        asserterror Src.GetDateValue(Obj, 'nope');
        Assert.ExpectedError('');
    end;

    [Test]
    procedure GetDateTime_ReturnsDateTimeValue()
    var
        Obj: JsonObject;
        Expected: DateTime;
    begin
        Expected := CreateDateTime(DMY2Date(15, 6, 2024), 120000T);
        Obj.Add('ts', Expected);
        Assert.AreEqual(Expected, Src.GetDateTimeValue(Obj, 'ts'), 'GetDateTime must return the stored datetime');
    end;

    [Test]
    procedure GetDateTime_MissingKey_Throws()
    var
        Obj: JsonObject;
    begin
        asserterror Src.GetDateTimeValue(Obj, 'nope');
        Assert.ExpectedError('');
    end;

    [Test]
    procedure WriteWithSecretsTo_ProducesValidJson()
    var
        Obj: JsonObject;
        Json: Text;
    begin
        Obj.Add('key', 'val');
        Json := Src.WriteWithSecretsToText(Obj);
        Assert.IsTrue(Json.StartsWith('{'), 'WriteWithSecretsTo must produce a JSON object string');
        Assert.IsTrue(Json.Contains('"key"'), 'JSON must contain the key');
        Assert.IsTrue(Json.Contains('"val"'), 'JSON must contain the value');
    end;
}
