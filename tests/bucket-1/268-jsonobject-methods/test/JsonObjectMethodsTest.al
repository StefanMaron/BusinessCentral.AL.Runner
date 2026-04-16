codeunit 84201 "JOM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JOM Src";

    [Test]
    procedure Add_And_Get_Text_RoundTrip()
    begin
        // Positive: Add a text property then Get it back.
        Assert.AreEqual('runner', Src.AddAndGet_Text(),
            'JsonObject.Add + Get must round-trip a text value');
    end;

    [Test]
    procedure Add_And_Get_Integer_RoundTrip()
    begin
        // Positive: Add an integer property then Get it back.
        Assert.AreEqual(42, Src.AddAndGet_Integer(),
            'JsonObject.Add + Get must round-trip an integer value');
    end;

    [Test]
    procedure Contains_Returns_True_For_Existing_Key()
    begin
        // Positive: Contains returns true when the key is present.
        Assert.IsTrue(Src.Contains_Existing(),
            'JsonObject.Contains must return true for an existing key');
    end;

    [Test]
    procedure Contains_Returns_False_For_Missing_Key()
    begin
        // Negative: Contains returns false when the key is absent.
        Assert.IsFalse(Src.Contains_Missing(),
            'JsonObject.Contains must return false for a missing key');
    end;

    [Test]
    procedure Keys_Returns_Correct_Count()
    begin
        // Positive: Keys() returns a list with one entry per property.
        Assert.AreEqual(3, Src.Keys_Count(),
            'JsonObject.Keys must return all property names');
    end;

    [Test]
    procedure Remove_Deletes_Property()
    begin
        // Positive: after Remove, Contains must return false.
        Assert.IsFalse(Src.Remove_Key(),
            'JsonObject.Remove must delete the property');
    end;

    [Test]
    procedure Replace_Updates_Value()
    begin
        // Positive: Replace must overwrite the existing value.
        Assert.AreEqual('new', Src.Replace_Value(),
            'JsonObject.Replace must update the property value');
    end;

    [Test]
    procedure Clone_Produces_Independent_Copy()
    begin
        // Positive: Clone must produce a deep copy independent of the original.
        Assert.IsTrue(Src.Clone_IsIndependent(),
            'JsonObject.Clone must produce an independent copy');
    end;

    [Test]
    procedure AsToken_Wraps_As_Object_Token()
    begin
        // Positive: AsToken must return a JsonToken that IsObject() is true.
        Assert.IsTrue(Src.AsToken_IsObject(),
            'JsonObject.AsToken must return an object token');
    end;

    [Test]
    procedure GetText_Returns_String_Value()
    begin
        // Positive: GetText must return the string value of the property.
        Assert.AreEqual('hello', Src.GetText_Key(),
            'JsonObject.GetText must return the text property value');
    end;

    [Test]
    procedure GetInteger_Returns_Integer_Value()
    begin
        // Positive: GetInteger must return the integer value of the property.
        Assert.AreEqual(99, Src.GetInteger_Key(),
            'JsonObject.GetInteger must return the integer property value');
    end;

    [Test]
    procedure GetDecimal_Returns_Decimal_Value()
    begin
        // Positive: GetDecimal must return the decimal value of the property.
        Assert.AreEqual(3.14, Src.GetDecimal_Key(),
            'JsonObject.GetDecimal must return the decimal property value');
    end;

    [Test]
    procedure GetObject_Returns_Nested_Object()
    begin
        // Positive: GetObject must return the nested JsonObject.
        Assert.IsTrue(Src.GetObject_Key(),
            'JsonObject.GetObject must return the nested object');
    end;

    [Test]
    procedure Get_Returns_False_For_Missing_Key()
    begin
        // Negative: Get must return false when the key does not exist.
        Assert.IsFalse(Src.Get_Missing(),
            'JsonObject.Get must return false for a missing key');
    end;
}
