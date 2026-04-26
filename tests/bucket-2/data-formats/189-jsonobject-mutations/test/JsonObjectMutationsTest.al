codeunit 60151 "JOM Mut Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JOM Mut Src";

    // --- Contains ---

    [Test]
    procedure Contains_ExistingKey_True()
    begin
        Assert.IsTrue(Src.HasKey(Src.BuildMixedObject(), 'name'),
            'Contains must return true for an added key');
    end;

    [Test]
    procedure Contains_MissingKey_False()
    begin
        Assert.IsFalse(Src.HasKey(Src.BuildMixedObject(), 'missing'),
            'Contains must return false for an absent key');
    end;

    // --- Add / Get ---

    [Test]
    procedure Add_Get_Text()
    begin
        Assert.AreEqual('Alice', Src.GetText(Src.BuildMixedObject(), 'name'),
            'Add("name", "Alice") + Get must return "Alice"');
    end;

    [Test]
    procedure Add_Get_Integer()
    begin
        Assert.AreEqual(30, Src.GetInteger(Src.BuildMixedObject(), 'age'),
            'Add("age", 30) + Get must return 30');
    end;

    [Test]
    procedure Add_Get_Boolean()
    begin
        Assert.IsTrue(Src.GetBoolean(Src.BuildMixedObject(), 'active'),
            'Add("active", true) + Get must return true');
    end;

    [Test]
    procedure Add_Get_Decimal()
    begin
        Assert.AreEqual(3.14, Src.GetDecimal(Src.BuildMixedObject(), 'rate'),
            'Add("rate", 3.14) + Get must return 3.14');
    end;

    [Test]
    procedure Get_Missing_ReturnsFalse()
    begin
        // Negative: AL's Get returns Boolean — false when key absent.
        Assert.IsFalse(Src.GetReturnsFalseForMissing(Src.BuildMixedObject(), 'nope'),
            'Get must return false for a missing key');
    end;

    // --- Clone ---

    [Test]
    procedure Clone_PreservesKeys()
    begin
        Assert.AreEqual('Alice', Src.GetText(Src.CloneObject(Src.BuildMixedObject()), 'name'),
            'Clone must preserve string keys and values');
    end;

    [Test]
    procedure Clone_IsIndependent()
    var
        orig: JsonObject;
        cloned: JsonObject;
    begin
        orig := Src.BuildMixedObject();
        cloned := Src.CloneObject(orig);
        // Mutating the clone must not affect the original.
        Src.ReplaceValue(cloned, 'name', 'Bob');
        Assert.AreEqual('Alice', Src.GetText(orig, 'name'),
            'Mutating the clone must not affect the original');
    end;

    // --- Keys ---

    [Test]
    procedure Keys_Count_Is4()
    begin
        Assert.AreEqual(4, Src.KeyCount(Src.BuildMixedObject()),
            'Keys on a 4-entry object must return 4 entries');
    end;

    [Test]
    procedure Keys_IncludesName()
    begin
        Assert.IsTrue(Src.HasSpecificKey(Src.BuildMixedObject(), 'name'),
            'Keys() must include "name"');
    end;

    [Test]
    procedure Keys_ExcludesAbsent()
    begin
        Assert.IsFalse(Src.HasSpecificKey(Src.BuildMixedObject(), 'ghost'),
            'Keys() must not include a key that was never added');
    end;

    // --- AsToken round-trip ---

    [Test]
    procedure AsToken_RoundTrip_PreservesObject()
    begin
        Assert.AreEqual('Alice', Src.AsTokenRoundTrip_ReturnsObject(Src.BuildMixedObject(), 'name'),
            'AsToken().AsObject() must return an object semantically equal to the original');
    end;

    // --- Mutation via Remove + Add ---

    [Test]
    procedure Replace_Via_RemoveAndAdd()
    var
        o: JsonObject;
    begin
        o := Src.BuildMixedObject();
        Src.ReplaceValue(o, 'name', 'Carol');
        Assert.AreEqual('Carol', Src.GetText(o, 'name'),
            'Remove+Add replaces a value while preserving the rest of the object');
    end;

    [Test]
    procedure Add_NotANoOp_NegativeTrap()
    var
        o: JsonObject;
    begin
        // Negative trap: Add must actually store — Contains reports true after Add.
        o.Add('x', 'y');
        Assert.AreNotEqual(false, Src.HasKey(o, 'x'),
            'Add must not be a no-op — Contains must report true for added key');
    end;
}
