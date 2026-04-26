codeunit 60141 "JAM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JAM Src";

    // --- Add ---

    [Test]
    procedure Add_ThreeMixed_CountIs3()
    begin
        Assert.AreEqual(3, Src.Count(Src.BuildThreeElementArray()),
            'Three Add calls must produce a 3-element array');
    end;

    [Test]
    procedure Add_IntegerStored()
    begin
        Assert.AreEqual(42, Src.FirstInt(Src.BuildThreeElementArray()),
            'Integer Add must be retrievable as Integer');
    end;

    [Test]
    procedure Add_TextStored()
    begin
        Assert.AreEqual('hello', Src.SecondText(Src.BuildThreeElementArray()),
            'Text Add must be retrievable as Text');
    end;

    [Test]
    procedure Add_BooleanStored()
    begin
        Assert.IsTrue(Src.ThirdBool(Src.BuildThreeElementArray()),
            'Boolean Add(true) must be retrievable as true');
    end;

    [Test]
    procedure Add_JsonObject()
    begin
        Assert.AreEqual('v', Src.NestedObjectKey(Src.AddJsonObject(), 0, 'k'),
            'Adding a JsonObject must preserve its key/value pairs');
    end;

    // --- Set ---

    [Test]
    procedure Set_ReplacesElement()
    var
        a: JsonArray;
    begin
        a := Src.BuildThreeElementArray();
        Src.SetItem_Int(a, 0, 99);
        Assert.AreEqual(99, Src.FirstInt(a),
            'Set(0, 99) must replace the first element');
    end;

    [Test]
    procedure Set_DoesNotChangeCount()
    var
        a: JsonArray;
    begin
        a := Src.BuildThreeElementArray();
        Src.SetItem_Int(a, 0, 99);
        Assert.AreEqual(3, Src.Count(a),
            'Set must not change the array length');
    end;

    // --- Insert ---

    [Test]
    procedure Insert_AtStart_IncreasesCount()
    var
        a: JsonArray;
    begin
        a := Src.BuildThreeElementArray();
        Src.InsertInt(a, 0, 7);
        Assert.AreEqual(4, Src.Count(a),
            'Insert must increase count by 1');
    end;

    [Test]
    procedure Insert_AtStart_ShiftsOriginal()
    var
        a: JsonArray;
    begin
        a := Src.BuildThreeElementArray();
        Src.InsertInt(a, 0, 7);
        // 42 (was at index 0) should now be at index 1.
        Assert.AreEqual(42, Src.IntAt(a, 1),
            'Insert at front must shift original elements right');
    end;

    [Test]
    procedure Insert_Middle_CorrectPosition()
    var
        a: JsonArray;
    begin
        a.Add(1);
        a.Add(3);
        Src.InsertInt(a, 1, 2);
        Assert.AreEqual(2, Src.IntAt(a, 1),
            'Insert(1, 2) must place 2 at index 1');
    end;

    // --- RemoveAt ---

    [Test]
    procedure RemoveAt_DecreasesCount()
    var
        a: JsonArray;
    begin
        a := Src.BuildThreeElementArray();
        Src.RemoveAt(a, 1);
        Assert.AreEqual(2, Src.Count(a),
            'RemoveAt must decrease count by 1');
    end;

    [Test]
    procedure RemoveAt_ShiftsRemaining()
    var
        a: JsonArray;
        t: JsonToken;
    begin
        a := Src.BuildThreeElementArray();
        Src.RemoveAt(a, 0);
        // After removing index 0, what was at 1 ("hello") is now at 0.
        a.Get(0, t);
        Assert.AreEqual('hello', t.AsValue().AsText(),
            'RemoveAt(0) must shift remaining elements left');
    end;

    [Test]
    procedure RemoveAt_NegativeTrap_NotANoop()
    var
        a: JsonArray;
    begin
        // Negative trap: RemoveAt must actually remove — count must change.
        a := Src.BuildThreeElementArray();
        Src.RemoveAt(a, 0);
        Assert.AreNotEqual(3, Src.Count(a),
            'RemoveAt must not be a no-op — count must decrease');
    end;

    // --- IndexOf ---

    [Test]
    procedure IndexOf_Int_Found()
    var
        a: JsonArray;
    begin
        a.Add(10);
        a.Add(20);
        a.Add(30);
        Assert.AreEqual(1, Src.IndexOfInt(a, 20),
            'IndexOf(20) must return index 1 (0-based)');
    end;

    [Test]
    procedure IndexOf_Int_NotFound_ReturnsNegative()
    var
        a: JsonArray;
    begin
        a.Add(10);
        a.Add(20);
        // AL convention for JsonArray.IndexOf: returns -1 when not found.
        Assert.AreEqual(-1, Src.IndexOfInt(a, 99),
            'IndexOf must return -1 when the value is absent');
    end;

    // --- Combined ---

    [Test]
    procedure Build_Insert_Remove_EndToEnd()
    var
        a: JsonArray;
    begin
        a.Add(1);
        a.Add(2);
        a.Add(3);
        Src.InsertInt(a, 0, 0);     // [0, 1, 2, 3]
        Src.RemoveAt(a, 3);          // [0, 1, 2]
        Src.SetItem_Int(a, 1, 99);   // [0, 99, 2]
        Assert.AreEqual(3, Src.Count(a), 'Final count must be 3');
        Assert.AreEqual(0, Src.IntAt(a, 0), 'Index 0 must be 0');
        Assert.AreEqual(99, Src.IntAt(a, 1), 'Index 1 must be 99');
        Assert.AreEqual(2, Src.IntAt(a, 2), 'Index 2 must be 2');
    end;
}
