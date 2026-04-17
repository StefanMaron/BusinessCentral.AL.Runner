codeunit 114001 "JAEX Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JAEX Src";

    // ── GetBigInteger ─────────────────────────────────────────────────────────

    [Test]
    procedure GetBigInteger_ReturnsStoredValue()
    var
        Arr: JsonArray;
        B: BigInteger;
    begin
        Evaluate(B, '9999999999');
        Arr.Add(B);
        Assert.AreEqual(B, Src.GetBigInteger(Arr, 0), 'GetBigInteger must return the stored BigInteger');
    end;

    [Test]
    procedure GetBigInteger_OutOfBounds_ThrowsError()
    var
        Arr: JsonArray;
    begin
        asserterror Src.GetBigInteger(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetByte ───────────────────────────────────────────────────────────────

    [Test]
    procedure GetByte_ReturnsStoredValue()
    var
        Arr: JsonArray;
        Val: Byte;
    begin
        Val := 200;
        Arr.Add(Val);
        Assert.AreEqual(Val, Src.GetByte(Arr, 0), 'GetByte must return the stored Byte');
    end;

    [Test]
    procedure GetByte_OutOfBounds_ThrowsError()
    var
        Arr: JsonArray;
    begin
        asserterror Src.GetByte(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetChar ───────────────────────────────────────────────────────────────

    [Test]
    procedure GetChar_ReturnsStoredValue()
    var
        Arr: JsonArray;
        Val: Char;
    begin
        Val := 65; // 'A'
        Arr.Add(Val);
        Assert.AreEqual(Val, Src.GetChar(Arr, 0), 'GetChar must return the stored Char');
    end;

    [Test]
    procedure GetChar_OutOfBounds_ThrowsError()
    var
        Arr: JsonArray;
    begin
        asserterror Src.GetChar(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetDate ───────────────────────────────────────────────────────────────

    [Test]
    procedure GetDate_ReturnsStoredValue()
    var
        Arr: JsonArray;
        D: Date;
    begin
        D := DMY2Date(15, 6, 2024);
        Arr.Add(D);
        Assert.AreEqual(D, Src.GetDate(Arr, 0), 'GetDate must return the stored Date');
    end;

    [Test]
    procedure GetDate_OutOfBounds_ThrowsError()
    var
        Arr: JsonArray;
    begin
        asserterror Src.GetDate(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetDateTime ───────────────────────────────────────────────────────────

    [Test]
    procedure GetDateTime_ReturnsStoredValue()
    var
        Arr: JsonArray;
        DT: DateTime;
    begin
        DT := CreateDateTime(DMY2Date(1, 3, 2025), 120000T);
        Arr.Add(DT);
        Assert.AreEqual(DT, Src.GetDateTime(Arr, 0), 'GetDateTime must return the stored DateTime');
    end;

    [Test]
    procedure GetDateTime_OutOfBounds_ThrowsError()
    var
        Arr: JsonArray;
    begin
        asserterror Src.GetDateTime(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetDuration ───────────────────────────────────────────────────────────

    [Test]
    procedure GetDuration_ReturnsStoredValue()
    var
        Arr: JsonArray;
        D: Duration;
    begin
        D := 5000;
        Arr.Add(D);
        Assert.AreEqual(D, Src.GetDuration(Arr, 0), 'GetDuration must return the stored Duration');
    end;

    [Test]
    procedure GetDuration_OutOfBounds_ThrowsError()
    var
        Arr: JsonArray;
    begin
        asserterror Src.GetDuration(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetOption ─────────────────────────────────────────────────────────────

    [Test]
    procedure GetOption_ReturnsStoredOrdinal()
    var
        Arr: JsonArray;
    begin
        Arr.Add(3);
        Assert.AreEqual(3, Src.GetOption(Arr, 0), 'GetOption must return the stored integer ordinal');
    end;

    [Test]
    procedure GetOption_OutOfBounds_ThrowsError()
    var
        Arr: JsonArray;
    begin
        asserterror Src.GetOption(Arr, 0);
        Assert.ExpectedError('');
    end;

    // ── GetTime ───────────────────────────────────────────────────────────────

    [Test]
    procedure GetTime_ReturnsStoredValue()
    var
        Arr: JsonArray;
    begin
        Arr.Add(153000T);
        Assert.AreEqual(153000T, Src.GetTime(Arr, 0), 'GetTime must return the stored Time');
    end;

    [Test]
    procedure GetTime_OutOfBounds_ThrowsError()
    var
        Arr: JsonArray;
    begin
        asserterror Src.GetTime(Arr, 0);
        Assert.ExpectedError('');
    end;
}
