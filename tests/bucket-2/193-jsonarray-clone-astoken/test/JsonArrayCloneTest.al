/// Tests for JsonArray.Clone() and JsonArray.AsToken().
codeunit 60251 "JAC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JAC Src";

    // ── Clone ──────────────────────────────────────────────────────────────

    [Test]
    procedure Clone_EmptyArray_ReturnsEmptyToken()
    var
        Arr: JsonArray;
        Token: JsonToken;
        CloneArr: JsonArray;
    begin
        // [GIVEN] an empty array
        // [WHEN] Clone is called
        Token := Src.CloneArray(Arr);
        CloneArr := Token.AsArray();
        // [THEN] clone is an array with 0 elements
        Assert.AreEqual(0, CloneArr.Count(), 'Clone of empty array must have count 0');
    end;

    [Test]
    procedure Clone_ArrayWithElements_CopiesCount()
    var
        Arr: JsonArray;
        Token: JsonToken;
        CloneArr: JsonArray;
    begin
        // [GIVEN] array with 3 elements
        Arr.Add('a');
        Arr.Add('b');
        Arr.Add('c');
        // [WHEN] Clone is called
        Token := Src.CloneArray(Arr);
        CloneArr := Token.AsArray();
        // [THEN] clone has the same count
        Assert.AreEqual(3, CloneArr.Count(), 'Clone must have same element count');
    end;

    [Test]
    procedure Clone_IsDeepCopy_OriginalMutationNotReflected()
    var
        Arr: JsonArray;
    begin
        // [GIVEN] array with 2 elements
        Arr.Add(10);
        Arr.Add(20);
        // [WHEN] Clone is taken and original is mutated
        // [THEN] clone count is still 2 (not 3)
        Assert.AreEqual(2, Src.CloneIsIndependent(Arr),
            'Clone must not reflect mutation of original after cloning');
    end;

    // ── AsToken ───────────────────────────────────────────────────────────

    [Test]
    procedure AsToken_EmptyArray_IsArray()
    var
        Arr: JsonArray;
    begin
        // [GIVEN] an empty array
        // [WHEN] AsToken is called
        // [THEN] the resulting token reports IsArray = true
        Assert.IsTrue(Src.AsTokenIsArray(Arr), 'AsToken() on JsonArray must yield IsArray=true');
    end;

    [Test]
    procedure AsToken_PreservesCount()
    var
        Arr: JsonArray;
    begin
        // [GIVEN] array with 2 elements
        Arr.Add(1);
        Arr.Add(2);
        // [WHEN] AsToken is called and array is recovered
        // [THEN] count is still 2
        Assert.AreEqual(2, Src.AsTokenCount(Arr), 'AsToken().AsArray() must preserve element count');
    end;

    [Test]
    procedure AsToken_IsNotValue()
    var
        Arr: JsonArray;
        Token: JsonToken;
    begin
        // [GIVEN/WHEN] empty array converted to token
        Token := Src.ArrayAsToken(Arr);
        // [THEN] IsValue = false
        Assert.IsFalse(Token.IsValue(), 'AsToken on JsonArray must not be a value');
    end;
}
