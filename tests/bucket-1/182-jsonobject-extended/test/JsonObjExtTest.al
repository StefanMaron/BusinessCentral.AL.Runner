codeunit 109001 "JOEX Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JOEX Src";

    // ── GetTime ────────────────────────────────────────────────────────────────

    [Test]
    procedure GetTime_ReturnsStoredTime()
    var
        Obj: JsonObject;
    begin
        Obj.Add('t', 120000T);
        Assert.AreEqual(120000T, Src.GetTime(Obj, 't'), 'GetTime must return the stored Time value');
    end;

    [Test]
    procedure GetTime_MissingKey_ThrowsError()
    var
        Obj: JsonObject;
    begin
        asserterror Src.GetTime(Obj, 'missing');
        Assert.ExpectedError('');
    end;

    // ── GetDuration ────────────────────────────────────────────────────────────

    [Test]
    procedure GetDuration_ReturnsStoredDuration()
    var
        Obj: JsonObject;
        D: Duration;
    begin
        D := 5000;
        Obj.Add('d', D);
        Assert.AreEqual(D, Src.GetDuration(Obj, 'd'), 'GetDuration must return the stored Duration value');
    end;

    [Test]
    procedure GetDuration_MissingKey_ThrowsError()
    var
        Obj: JsonObject;
    begin
        asserterror Src.GetDuration(Obj, 'missing');
        Assert.ExpectedError('');
    end;

    // ── GetOption ─────────────────────────────────────────────────────────────

    [Test]
    procedure GetOption_ReturnsOrdinal()
    var
        Obj: JsonObject;
    begin
        Obj.Add('opt', 2);
        Assert.AreEqual(2, Src.GetOption(Obj, 'opt'), 'GetOption must return the integer ordinal');
    end;

    [Test]
    procedure GetOption_MissingKey_ThrowsError()
    var
        Obj: JsonObject;
    begin
        asserterror Src.GetOption(Obj, 'missing');
        Assert.ExpectedError('');
    end;

    // ── GetByte ───────────────────────────────────────────────────────────────

    [Test]
    procedure GetByte_ReturnsStoredValue()
    var
        Obj: JsonObject;
        B: Byte;
    begin
        B := 200;
        Obj.Add('b', B);
        Assert.AreEqual(B, Src.GetByte(Obj, 'b'), 'GetByte must return the stored Byte value');
    end;

    [Test]
    procedure GetByte_MissingKey_ThrowsError()
    var
        Obj: JsonObject;
    begin
        asserterror Src.GetByte(Obj, 'missing');
        Assert.ExpectedError('');
    end;

    // ── GetBigInteger ─────────────────────────────────────────────────────────

    [Test]
    procedure GetBigInteger_ReturnsStoredValue()
    var
        Obj: JsonObject;
        B: BigInteger;
    begin
        Evaluate(B, '9999999999');
        Obj.Add('b', B);
        Assert.AreEqual(B, Src.GetBigInteger(Obj, 'b'), 'GetBigInteger must return the stored BigInteger value');
    end;

    [Test]
    procedure GetBigInteger_MissingKey_ThrowsError()
    var
        Obj: JsonObject;
    begin
        asserterror Src.GetBigInteger(Obj, 'missing');
        Assert.ExpectedError('');
    end;

    // ── Values ────────────────────────────────────────────────────────────────

    [Test]
    procedure Values_EmptyObject_ReturnsZero()
    var
        Obj: JsonObject;
    begin
        Assert.AreEqual(0, Src.ValuesCount(Obj), 'Values on empty object must return 0');
    end;

    [Test]
    procedure Values_TwoEntries_ReturnsTwo()
    var
        Obj: JsonObject;
    begin
        Obj.Add('a', 1);
        Obj.Add('b', 2);
        Assert.AreEqual(2, Src.ValuesCount(Obj), 'Values must return 2 tokens for object with 2 entries');
    end;

    // ── Path ──────────────────────────────────────────────────────────────────

    [Test]
    procedure Path_RootObject_ReturnsDollar()
    var
        Obj: JsonObject;
    begin
        Assert.AreEqual('$', Src.PathRoot(Obj), 'Path() on root object must return "$"');
    end;

    // ── WriteToYaml ───────────────────────────────────────────────────────────

    [Test]
    procedure WriteToYaml_ProducesNonEmptyOutput()
    var
        Obj: JsonObject;
        Result: Text;
    begin
        Obj.Add('k', 'v');
        Result := Src.WriteToYamlText(Obj);
        Assert.IsTrue(Result <> '', 'WriteToYaml must produce non-empty output');
    end;

    // ── ReadFromYaml ──────────────────────────────────────────────────────────

    [Test]
    procedure ReadFromYaml_JsonCompatibleInput_ParsesKey()
    var
        Result: Text;
    begin
        Result := Src.ReadFromYamlGetText('{"key": "value"}', 'key');
        Assert.AreEqual('value', Result, 'ReadFromYaml must parse JSON-compatible YAML input');
    end;

}
