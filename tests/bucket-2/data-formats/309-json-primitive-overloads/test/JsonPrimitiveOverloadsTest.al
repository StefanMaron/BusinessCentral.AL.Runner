/// Tests for Json.* per-primitive-type overloads:
/// JsonArray.IndexOf, JsonArray.Insert, JsonArray.Set,
/// JsonObject.Replace, and JsonValue.SetValue.
/// All 71 typed overloads route through NavJsonToken implicit-conversion
/// in the BC runtime (no TrappableOperationExecutor path).
codeunit 309000 "Json Primitive Overloads Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── JsonArray.IndexOf ─────────────────────────────────────────

    [Test]
    procedure IndexOf_Integer_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        Idx: Integer;
    begin
        // [GIVEN] Array containing two integers
        JArr.Add(10);
        JArr.Add(20);
        // [WHEN] IndexOf called with Integer value
        Idx := JArr.IndexOf(20);
        // [THEN] Returns 1-based ... actually 0-based index 1
        Assert.AreEqual(1, Idx, 'IndexOf(Integer) should return index 1');
    end;

    [Test]
    procedure IndexOf_Integer_NotFound_ReturnsMinus1()
    var
        JArr: JsonArray;
        Idx: Integer;
    begin
        JArr.Add(10);
        JArr.Add(20);
        Idx := JArr.IndexOf(99);
        Assert.AreEqual(-1, Idx, 'IndexOf(Integer) not-found should return -1');
    end;

    [Test]
    procedure IndexOf_Text_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        Idx: Integer;
    begin
        // [GIVEN] Array containing two text values
        JArr.Add('alpha');
        JArr.Add('beta');
        // [WHEN] IndexOf called with Text value
        Idx := JArr.IndexOf('beta');
        // [THEN] Returns index 1
        Assert.AreEqual(1, Idx, 'IndexOf(Text) should return index 1');
    end;

    [Test]
    procedure IndexOf_Text_NotFound_ReturnsMinus1()
    var
        JArr: JsonArray;
        Idx: Integer;
    begin
        JArr.Add('alpha');
        Idx := JArr.IndexOf('missing');
        Assert.AreEqual(-1, Idx, 'IndexOf(Text) not-found should return -1');
    end;

    [Test]
    procedure IndexOf_Boolean_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        Idx: Integer;
    begin
        JArr.Add(false);
        JArr.Add(true);
        Idx := JArr.IndexOf(true);
        Assert.AreEqual(1, Idx, 'IndexOf(Boolean) should return index 1');
    end;

    [Test]
    procedure IndexOf_Decimal_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        D: Decimal;
        Idx: Integer;
    begin
        D := 3.14;
        JArr.Add(1.5);
        JArr.Add(D);
        Idx := JArr.IndexOf(D);
        Assert.AreEqual(1, Idx, 'IndexOf(Decimal) should return index 1');
    end;

    [Test]
    procedure IndexOf_Date_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        D: Date;
        Idx: Integer;
    begin
        D := 20240101D;
        JArr.Add(19900101D);
        JArr.Add(D);
        Idx := JArr.IndexOf(D);
        Assert.AreEqual(1, Idx, 'IndexOf(Date) should return index 1');
    end;

    [Test]
    procedure IndexOf_Time_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        T: Time;
        Idx: Integer;
    begin
        T := 120000T;
        JArr.Add(080000T);
        JArr.Add(T);
        Idx := JArr.IndexOf(T);
        Assert.AreEqual(1, Idx, 'IndexOf(Time) should return index 1');
    end;

    [Test]
    procedure IndexOf_DateTime_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        DT: DateTime;
        Idx: Integer;
    begin
        DT := CreateDateTime(20240101D, 120000T);
        JArr.Add(CreateDateTime(20230101D, 080000T));
        JArr.Add(DT);
        Idx := JArr.IndexOf(DT);
        Assert.AreEqual(1, Idx, 'IndexOf(DateTime) should return index 1');
    end;

    [Test]
    procedure IndexOf_Duration_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        Dur: Duration;
        Idx: Integer;
    begin
        Dur := 7200000;
        JArr.Add(3600000);
        JArr.Add(Dur);
        Idx := JArr.IndexOf(Dur);
        Assert.AreEqual(1, Idx, 'IndexOf(Duration) should return index 1');
    end;

    [Test]
    procedure IndexOf_Option_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        Opt: Option A, B, C;
        Idx: Integer;
    begin
        Opt := Opt::B;
        JArr.Add(Opt::A);
        JArr.Add(Opt);
        Idx := JArr.IndexOf(Opt);
        Assert.AreEqual(1, Idx, 'IndexOf(Option) should return index 1');
    end;

    [Test]
    procedure IndexOf_Byte_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        B: Byte;
        Idx: Integer;
    begin
        B := 200;
        JArr.Add(100);
        JArr.Add(B);
        Idx := JArr.IndexOf(B);
        Assert.AreEqual(1, Idx, 'IndexOf(Byte) should return index 1');
    end;

    [Test]
    procedure IndexOf_JsonObject_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        JObj1: JsonObject;
        JObj2: JsonObject;
        Idx: Integer;
    begin
        JObj1.Add('id', 1);
        JObj2.Add('id', 2);
        JArr.Add(JObj1);
        JArr.Add(JObj2);
        Idx := JArr.IndexOf(JObj2);
        Assert.AreEqual(1, Idx, 'IndexOf(JsonObject) should return index 1');
    end;

    [Test]
    procedure IndexOf_JsonArray_ReturnsCorrectIndex()
    var
        JArr: JsonArray;
        JInner1: JsonArray;
        JInner2: JsonArray;
        Idx: Integer;
    begin
        JInner1.Add(1);
        JInner2.Add(2);
        JArr.Add(JInner1);
        JArr.Add(JInner2);
        Idx := JArr.IndexOf(JInner2);
        Assert.AreEqual(1, Idx, 'IndexOf(JsonArray) should return index 1');
    end;

    // ── JsonArray.Insert ─────────────────────────────────────────

    [Test]
    procedure Insert_Integer_ShiftsElements()
    var
        JArr: JsonArray;
        JTok: JsonToken;
    begin
        // [GIVEN] Array [1, 3]
        JArr.Add(1);
        JArr.Add(3);
        // [WHEN] Insert 2 at index 1
        JArr.Insert(1, 2);
        // [THEN] Element at index 1 is 2; count is 3
        JArr.Get(1, JTok);
        Assert.AreEqual(2, JTok.AsValue().AsInteger(), 'Insert(Integer) should place value at index 1');
        Assert.AreEqual(3, JArr.Count(), 'Array count should be 3 after insert');
    end;

    [Test]
    procedure Insert_Text_AtStart()
    var
        JArr: JsonArray;
        JTok: JsonToken;
    begin
        // [GIVEN] Array ['b', 'c']
        JArr.Add('b');
        JArr.Add('c');
        // [WHEN] Insert 'a' at index 0
        JArr.Insert(0, 'a');
        // [THEN] Element at index 0 is 'a'
        JArr.Get(0, JTok);
        Assert.AreEqual('a', JTok.AsValue().AsText(), 'Insert(Text) should place value at index 0');
    end;

    [Test]
    procedure Insert_Boolean_AtIndex()
    var
        JArr: JsonArray;
        JTok: JsonToken;
    begin
        JArr.Add(false);
        JArr.Insert(0, true);
        JArr.Get(0, JTok);
        Assert.IsTrue(JTok.AsValue().AsBoolean(), 'Insert(Boolean) true at index 0');
    end;

    [Test]
    procedure Insert_Decimal_AtIndex()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        D: Decimal;
    begin
        D := 3.14;
        JArr.Add(1.0);
        JArr.Add(5.0);
        JArr.Insert(1, D);
        JArr.Get(1, JTok);
        Assert.AreEqual(3.14, JTok.AsValue().AsDecimal(), 'Insert(Decimal) should place value at index 1');
    end;

    [Test]
    procedure Insert_Date_AtIndex()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        D: Date;
    begin
        D := 20240101D;
        JArr.Add(20230101D);
        JArr.Insert(0, D);
        JArr.Get(0, JTok);
        Assert.AreEqual(20240101D, JTok.AsValue().AsDate(), 'Insert(Date) should place value at index 0');
    end;

    [Test]
    procedure Insert_DateTime_AtIndex()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        DT: DateTime;
    begin
        DT := CreateDateTime(20240101D, 120000T);
        JArr.Add(CreateDateTime(20230101D, 080000T));
        JArr.Insert(0, DT);
        JArr.Get(0, JTok);
        Assert.AreEqual(DT, JTok.AsValue().AsDateTime(), 'Insert(DateTime) should place value at index 0');
    end;

    [Test]
    procedure Insert_Duration_AtIndex()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        Dur: Duration;
    begin
        Dur := 3600000;
        JArr.Add(0);
        JArr.Insert(1, Dur);
        JArr.Get(1, JTok);
        Assert.AreEqual(3600000, JTok.AsValue().AsInteger(), 'Insert(Duration) should place value at index 1');
    end;

    [Test]
    procedure Insert_Option_AtIndex()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        Opt: Option X, Y, Z;
    begin
        Opt := Opt::Z;
        JArr.Add(Opt::X);
        JArr.Insert(0, Opt);
        JArr.Get(0, JTok);
        Assert.AreEqual(2, JTok.AsValue().AsInteger(), 'Insert(Option) Z=2 at index 0');
    end;

    [Test]
    procedure Insert_JsonObject_AtIndex()
    var
        JArr: JsonArray;
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('key', 'val');
        JArr.Add('placeholder');
        JArr.Insert(0, JObj);
        JArr.Get(0, JTok);
        Assert.IsTrue(JTok.IsObject(), 'Insert(JsonObject) should place object at index 0');
    end;

    [Test]
    procedure Insert_JsonArray_AtIndex()
    var
        JArr: JsonArray;
        JInner: JsonArray;
        JTok: JsonToken;
    begin
        JInner.Add(99);
        JArr.Add('placeholder');
        JArr.Insert(0, JInner);
        JArr.Get(0, JTok);
        Assert.IsTrue(JTok.IsArray(), 'Insert(JsonArray) should place array at index 0');
    end;

    // ── JsonArray.Set ─────────────────────────────────────────────

    [Test]
    procedure Set_Integer_ReplacesElement()
    var
        JArr: JsonArray;
        JTok: JsonToken;
    begin
        // [GIVEN] Array [10, 20]
        JArr.Add(10);
        JArr.Add(20);
        // [WHEN] Set index 0 to 99
        JArr.Set(0, 99);
        // [THEN] Element at index 0 is 99; count unchanged
        JArr.Get(0, JTok);
        Assert.AreEqual(99, JTok.AsValue().AsInteger(), 'Set(Integer) should replace element at index 0');
        Assert.AreEqual(2, JArr.Count(), 'Count should remain 2 after Set');
    end;

    [Test]
    procedure Set_Text_ReplacesElement()
    var
        JArr: JsonArray;
        JTok: JsonToken;
    begin
        JArr.Add('old');
        JArr.Add('keep');
        JArr.Set(0, 'new');
        JArr.Get(0, JTok);
        Assert.AreEqual('new', JTok.AsValue().AsText(), 'Set(Text) should replace element');
    end;

    [Test]
    procedure Set_Boolean_ReplacesElement()
    var
        JArr: JsonArray;
        JTok: JsonToken;
    begin
        JArr.Add(false);
        JArr.Set(0, true);
        JArr.Get(0, JTok);
        Assert.IsTrue(JTok.AsValue().AsBoolean(), 'Set(Boolean) true should replace false');
    end;

    [Test]
    procedure Set_Decimal_ReplacesElement()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        D: Decimal;
    begin
        D := 9.99;
        JArr.Add(0.0);
        JArr.Set(0, D);
        JArr.Get(0, JTok);
        Assert.AreEqual(9.99, JTok.AsValue().AsDecimal(), 'Set(Decimal) should replace element');
    end;

    [Test]
    procedure Set_Date_ReplacesElement()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        D: Date;
    begin
        D := 20240601D;
        JArr.Add(20230101D);
        JArr.Set(0, D);
        JArr.Get(0, JTok);
        Assert.AreEqual(20240601D, JTok.AsValue().AsDate(), 'Set(Date) should replace element');
    end;

    [Test]
    procedure Set_DateTime_ReplacesElement()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        DT: DateTime;
    begin
        DT := CreateDateTime(20240601D, 090000T);
        JArr.Add(CreateDateTime(20230101D, 080000T));
        JArr.Set(0, DT);
        JArr.Get(0, JTok);
        Assert.AreEqual(DT, JTok.AsValue().AsDateTime(), 'Set(DateTime) should replace element');
    end;

    [Test]
    procedure Set_Duration_ReplacesElement()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        Dur: Duration;
    begin
        Dur := 9000000;
        JArr.Add(0);
        JArr.Set(0, Dur);
        JArr.Get(0, JTok);
        Assert.AreEqual(9000000, JTok.AsValue().AsInteger(), 'Set(Duration) should replace element');
    end;

    [Test]
    procedure Set_Time_ReplacesElement()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        T: Time;
    begin
        T := 153000T;
        JArr.Add(080000T);
        JArr.Set(0, T);
        JArr.Get(0, JTok);
        Assert.AreEqual(T, JTok.AsValue().AsTime(), 'Set(Time) should replace element');
    end;

    [Test]
    procedure Set_Option_ReplacesElement()
    var
        JArr: JsonArray;
        JTok: JsonToken;
        Opt: Option P, Q, R;
    begin
        Opt := Opt::R;
        JArr.Add(Opt::P);
        JArr.Set(0, Opt);
        JArr.Get(0, JTok);
        Assert.AreEqual(2, JTok.AsValue().AsInteger(), 'Set(Option) R=2 should replace element');
    end;

    [Test]
    procedure Set_JsonObject_ReplacesElement()
    var
        JArr: JsonArray;
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('x', 1);
        JArr.Add('placeholder');
        JArr.Set(0, JObj);
        JArr.Get(0, JTok);
        Assert.IsTrue(JTok.IsObject(), 'Set(JsonObject) should replace element with object');
    end;

    [Test]
    procedure Set_JsonArray_ReplacesElement()
    var
        JArr: JsonArray;
        JInner: JsonArray;
        JTok: JsonToken;
    begin
        JInner.Add(42);
        JArr.Add('placeholder');
        JArr.Set(0, JInner);
        JArr.Get(0, JTok);
        Assert.IsTrue(JTok.IsArray(), 'Set(JsonArray) should replace element with array');
    end;

    // ── JsonObject.Replace ────────────────────────────────────────

    [Test]
    procedure Replace_Integer_UpdatesValue()
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        // [GIVEN] Object with key 'count' = 5
        JObj.Add('count', 5);
        // [WHEN] Replace 'count' with 42
        JObj.Replace('count', 42);
        // [THEN] New value is 42
        JObj.Get('count', JTok);
        Assert.AreEqual(42, JTok.AsValue().AsInteger(), 'Replace(Integer) should update value to 42');
    end;

    [Test]
    procedure Replace_Text_UpdatesValue()
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('name', 'old');
        JObj.Replace('name', 'new');
        JObj.Get('name', JTok);
        Assert.AreEqual('new', JTok.AsValue().AsText(), 'Replace(Text) should update value');
    end;

    [Test]
    procedure Replace_Boolean_UpdatesValue()
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('flag', false);
        JObj.Replace('flag', true);
        JObj.Get('flag', JTok);
        Assert.IsTrue(JTok.AsValue().AsBoolean(), 'Replace(Boolean) should update to true');
    end;

    [Test]
    procedure Replace_Decimal_UpdatesValue()
    var
        JObj: JsonObject;
        JTok: JsonToken;
        D: Decimal;
    begin
        D := 7.5;
        JObj.Add('price', 1.0);
        JObj.Replace('price', D);
        JObj.Get('price', JTok);
        Assert.AreEqual(7.5, JTok.AsValue().AsDecimal(), 'Replace(Decimal) should update value');
    end;

    [Test]
    procedure Replace_Date_UpdatesValue()
    var
        JObj: JsonObject;
        JTok: JsonToken;
        D: Date;
    begin
        D := 20240101D;
        JObj.Add('date', 20230101D);
        JObj.Replace('date', D);
        JObj.Get('date', JTok);
        Assert.AreEqual(20240101D, JTok.AsValue().AsDate(), 'Replace(Date) should update value');
    end;

    [Test]
    procedure Replace_DateTime_UpdatesValue()
    var
        JObj: JsonObject;
        JTok: JsonToken;
        DT: DateTime;
    begin
        DT := CreateDateTime(20240601D, 120000T);
        JObj.Add('stamp', CreateDateTime(20230101D, 080000T));
        JObj.Replace('stamp', DT);
        JObj.Get('stamp', JTok);
        Assert.AreEqual(DT, JTok.AsValue().AsDateTime(), 'Replace(DateTime) should update value');
    end;

    [Test]
    procedure Replace_Duration_UpdatesValue()
    var
        JObj: JsonObject;
        JTok: JsonToken;
        Dur: Duration;
    begin
        Dur := 1800000;
        JObj.Add('dur', 0);
        JObj.Replace('dur', Dur);
        JObj.Get('dur', JTok);
        Assert.AreEqual(1800000, JTok.AsValue().AsInteger(), 'Replace(Duration) should update value');
    end;

    [Test]
    procedure Replace_Time_UpdatesValue()
    var
        JObj: JsonObject;
        JTok: JsonToken;
        T: Time;
    begin
        T := 153000T;
        JObj.Add('time', 080000T);
        JObj.Replace('time', T);
        JObj.Get('time', JTok);
        Assert.AreEqual(T, JTok.AsValue().AsTime(), 'Replace(Time) should update value');
    end;

    [Test]
    procedure Replace_Option_UpdatesValue()
    var
        JObj: JsonObject;
        JTok: JsonToken;
        Opt: Option X, Y, Z;
    begin
        Opt := Opt::Z;
        JObj.Add('opt', Opt::X);
        JObj.Replace('opt', Opt);
        JObj.Get('opt', JTok);
        Assert.AreEqual(2, JTok.AsValue().AsInteger(), 'Replace(Option) Z=2 should update value');
    end;

    [Test]
    procedure Replace_JsonArray_UpdatesValue()
    var
        JObj: JsonObject;
        JArr: JsonArray;
        JTok: JsonToken;
    begin
        JArr.Add(1);
        JArr.Add(2);
        JObj.Add('items', 'placeholder');
        JObj.Replace('items', JArr);
        JObj.Get('items', JTok);
        Assert.IsTrue(JTok.IsArray(), 'Replace(JsonArray) should update to array type');
    end;

    [Test]
    procedure Replace_JsonObject_UpdatesValue()
    var
        JObj: JsonObject;
        JInner: JsonObject;
        JTok: JsonToken;
    begin
        JInner.Add('nested', true);
        JObj.Add('child', 'placeholder');
        JObj.Replace('child', JInner);
        JObj.Get('child', JTok);
        Assert.IsTrue(JTok.IsObject(), 'Replace(JsonObject) should update to object type');
    end;

    // ── JsonValue.SetValue ────────────────────────────────────────

    [Test]
    procedure SetValue_Integer_RoundTrips()
    var
        JVal: JsonValue;
    begin
        // [GIVEN/WHEN] SetValue with integer 42
        JVal.SetValue(42);
        // [THEN] AsInteger returns 42
        Assert.AreEqual(42, JVal.AsInteger(), 'SetValue(Integer) should round-trip as integer');
    end;

    [Test]
    procedure SetValue_Integer_NonDefault_ProvesMockIsLive()
    var
        JVal: JsonValue;
    begin
        // Prove that mock is not returning a default 0
        JVal.SetValue(12345);
        Assert.AreEqual(12345, JVal.AsInteger(), 'SetValue(Integer) 12345 should not return default 0');
    end;

    [Test]
    procedure SetValue_Text_RoundTrips()
    var
        JVal: JsonValue;
    begin
        JVal.SetValue('hello');
        Assert.AreEqual('hello', JVal.AsText(), 'SetValue(Text) should round-trip');
    end;

    [Test]
    procedure SetValue_Boolean_True_RoundTrips()
    var
        JVal: JsonValue;
    begin
        JVal.SetValue(true);
        Assert.IsTrue(JVal.AsBoolean(), 'SetValue(Boolean) true should round-trip');
    end;

    [Test]
    procedure SetValue_Boolean_False_RoundTrips()
    var
        JVal: JsonValue;
    begin
        JVal.SetValue(false);
        Assert.IsFalse(JVal.AsBoolean(), 'SetValue(Boolean) false should round-trip');
    end;

    [Test]
    procedure SetValue_Decimal_RoundTrips()
    var
        JVal: JsonValue;
        D: Decimal;
    begin
        D := 12.34;
        JVal.SetValue(D);
        Assert.AreEqual(12.34, JVal.AsDecimal(), 'SetValue(Decimal) should round-trip');
    end;

    [Test]
    procedure SetValue_Date_RoundTrips()
    var
        JVal: JsonValue;
        D: Date;
    begin
        D := 20240101D;
        JVal.SetValue(D);
        Assert.AreEqual(20240101D, JVal.AsDate(), 'SetValue(Date) should round-trip');
    end;

    [Test]
    procedure SetValue_DateTime_RoundTrips()
    var
        JVal: JsonValue;
        DT: DateTime;
    begin
        DT := CreateDateTime(20240601D, 120000T);
        JVal.SetValue(DT);
        Assert.AreEqual(DT, JVal.AsDateTime(), 'SetValue(DateTime) should round-trip');
    end;

    [Test]
    procedure SetValue_Time_RoundTrips()
    var
        JVal: JsonValue;
        T: Time;
    begin
        T := 153000T;
        JVal.SetValue(T);
        Assert.AreEqual(153000T, JVal.AsTime(), 'SetValue(Time) should round-trip');
    end;

    [Test]
    procedure SetValue_Duration_RoundTrips()
    var
        JVal: JsonValue;
        Dur: Duration;
    begin
        Dur := 3600000;
        JVal.SetValue(Dur);
        Assert.AreEqual(3600000, JVal.AsInteger(), 'SetValue(Duration) should round-trip as integer ms');
    end;

    [Test]
    procedure SetValue_Byte_RoundTrips()
    var
        JVal: JsonValue;
        B: Byte;
    begin
        B := 255;
        JVal.SetValue(B);
        Assert.AreEqual(255, JVal.AsInteger(), 'SetValue(Byte) 255 should round-trip as integer');
    end;

    [Test]
    procedure SetValue_Option_RoundTrips()
    var
        JVal: JsonValue;
        Opt: Option Low, Medium, High;
    begin
        Opt := Opt::High;
        JVal.SetValue(Opt);
        Assert.AreEqual(2, JVal.AsInteger(), 'SetValue(Option) High=2 should round-trip as integer');
    end;

    [Test]
    procedure SetValueToNull_Makes_IsNull_True()
    var
        JVal: JsonValue;
    begin
        JVal.SetValue(42);
        JVal.SetValueToNull();
        Assert.IsTrue(JVal.IsNull(), 'SetValueToNull should make IsNull() return true');
    end;

    [Test]
    procedure SetValueToUndefined_Makes_IsUndefined_True()
    var
        JVal: JsonValue;
    begin
        JVal.SetValue(42);
        JVal.SetValueToUndefined();
        Assert.IsTrue(JVal.IsUndefined(), 'SetValueToUndefined should make IsUndefined() return true');
    end;
}
