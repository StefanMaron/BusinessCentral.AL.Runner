codeunit 59351 "JAC Count Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JAC Count Src";

    [Test]
    procedure Count_EmptyArray_IsZero()
    var
        arr: JsonArray;
    begin
        // Positive: Count() on an uninitialised JsonArray must return 0.
        Assert.AreEqual(0, Src.GetCount(arr), 'empty array count must be 0');
    end;

    [Test]
    procedure Count_AfterThreeAdds_IsThree()
    var
        arr: JsonArray;
    begin
        arr.Add('first');
        arr.Add('second');
        arr.Add('third');
        Assert.AreEqual(3, Src.GetCount(arr), 'count after 3 Adds must be 3');
    end;

    [Test]
    procedure Count_AfterOneAdd_IsOne()
    var
        arr: JsonArray;
    begin
        arr.Add('only');
        Assert.AreEqual(1, Src.GetCount(arr), 'count after single Add must be 1');
    end;

    [Test]
    procedure Count_MatchesBuilderArg()
    begin
        // Proving: BuildWith(n) produces an array of exactly n elements.
        Assert.AreEqual(0, Src.GetCount(Src.BuildWith(0)), 'BuildWith(0) count');
        Assert.AreEqual(5, Src.GetCount(Src.BuildWith(5)), 'BuildWith(5) count');
        Assert.AreEqual(10, Src.GetCount(Src.BuildWith(10)), 'BuildWith(10) count');
    end;

    [Test]
    procedure Count_MixedTypes_CountsAllTokens()
    begin
        // Count must count all entries regardless of token type (text, integer,
        // boolean, decimal) — four Adds = 4.
        Assert.AreEqual(4, Src.GetCount(Src.BuildMixed()),
            'mixed-type array with 4 Adds must have Count=4');
    end;

    [Test]
    procedure Count_NestedStructures_CountsTopLevelOnly()
    var
        arr: JsonArray;
    begin
        // Nested JsonArray + JsonObject count as 1 token each at the top level,
        // so the outer array has 3 entries (not the 7 you'd get if Count recursed).
        arr := Src.BuildNested();
        Assert.AreEqual(3, Src.GetCount(arr),
            'top-level count must be 3; Count must not recurse into children');
    end;

    [Test]
    procedure Count_NotBuilderArgTimesTwo_NegativeTrap()
    begin
        // Negative: guard against a Count() that accidentally returns n*2 or similar.
        Assert.AreNotEqual(10, Src.GetCount(Src.BuildWith(5)),
            'Count of 5-element array must not be 10');
    end;

    [Test]
    procedure Count_NotFixedConstant_NegativeTrap()
    begin
        // Negative: guard against a Count() stub that always returns the same value.
        Assert.AreNotEqual(Src.GetCount(Src.BuildWith(3)), Src.GetCount(Src.BuildWith(7)),
            'Count must differ for arrays of different sizes');
    end;
}
