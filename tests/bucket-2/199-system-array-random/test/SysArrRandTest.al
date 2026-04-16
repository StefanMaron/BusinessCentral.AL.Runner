/// Tests for System built-ins: CompressArray, CopyArray,
/// CreateGuid, Random, Randomize.
codeunit 97501 "SAR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "SAR Src";

    // ── CompressArray ─────────────────────────────────────────────────────────

    [Test]
    procedure CompressArray_PacksNonBlanks()
    var
        Arr: array[5] of Text;
    begin
        Arr[1] := 'a';
        Arr[2] := '';
        Arr[3] := 'b';
        Arr[4] := '';
        Arr[5] := 'c';
        H.CompressTextArray(Arr);
        Assert.AreEqual('a', Arr[1], 'first non-blank must be at [1]');
        Assert.AreEqual('b', Arr[2], 'second non-blank must be at [2]');
        Assert.AreEqual('c', Arr[3], 'third non-blank must be at [3]');
        Assert.AreEqual('', Arr[4], 'trailing slot must be empty');
        Assert.AreEqual('', Arr[5], 'trailing slot must be empty');
    end;

    [Test]
    procedure CompressArray_AllBlank_StaysBlank()
    var
        Arr: array[5] of Text;
    begin
        H.CompressTextArray(Arr);
        Assert.AreEqual('', Arr[1], 'all-blank array stays all-blank after compress');
    end;

    [Test]
    procedure CompressArray_NoGap_Unchanged()
    var
        Arr: array[5] of Text;
    begin
        Arr[1] := 'x';
        Arr[2] := 'y';
        H.CompressTextArray(Arr);
        Assert.AreEqual('x', Arr[1], 'no-gap: first element unchanged');
        Assert.AreEqual('y', Arr[2], 'no-gap: second element unchanged');
    end;

    // ── CopyArray ─────────────────────────────────────────────────────────────

    [Test]
    procedure CopyArray_CopiesElements()
    var
        FromArr: array[5] of Integer;
        ToArr: array[5] of Integer;
    begin
        FromArr[1] := 10;
        FromArr[2] := 20;
        FromArr[3] := 30;
        H.CopyIntArray(FromArr, ToArr, 3);
        Assert.AreEqual(10, ToArr[1], 'first copied element must be 10');
        Assert.AreEqual(20, ToArr[2], 'second copied element must be 20');
        Assert.AreEqual(30, ToArr[3], 'third copied element must be 30');
    end;

    [Test]
    procedure CopyArray_DestZeroAfterCount()
    var
        FromArr: array[5] of Integer;
        ToArr: array[5] of Integer;
    begin
        FromArr[1] := 7;
        FromArr[2] := 8;
        ToArr[3] := 99;
        H.CopyIntArray(FromArr, ToArr, 2);
        Assert.AreEqual(7, ToArr[1], 'element 1 copied');
        Assert.AreEqual(8, ToArr[2], 'element 2 copied');
        // element 3 was not overwritten by CopyArray(count=2)
        Assert.AreEqual(99, ToArr[3], 'element beyond count must not be touched');
    end;

    // ── CreateGuid ────────────────────────────────────────────────────────────

    [Test]
    procedure CreateGuid_NonNull()
    var
        G: Guid;
    begin
        G := H.NewGuid();
        Assert.IsFalse(IsNullGuid(G), 'CreateGuid must return a non-null GUID');
    end;

    [Test]
    procedure CreateGuid_Unique()
    var
        G1: Guid;
        G2: Guid;
    begin
        G1 := H.NewGuid();
        G2 := H.NewGuid();
        Assert.AreNotEqual(Format(G1), Format(G2), 'two CreateGuid calls must return different GUIDs');
    end;

    // ── Random ────────────────────────────────────────────────────────────────

    [Test]
    procedure Random_InRange()
    var
        R: Integer;
    begin
        R := H.Rnd(100);
        Assert.IsTrue((R >= 1) and (R <= 100),
            'Random(100) must return value in [1, 100]');
    end;

    [Test]
    procedure Random_NotZero()
    var
        R: Integer;
    begin
        R := H.Rnd(100);
        Assert.AreNotEqual(0, R, 'Random(100) must not return 0 (BC returns [1..MaxValue])');
    end;

    // ── Randomize ────────────────────────────────────────────────────────────

    [Test]
    procedure Randomize_WithSeed_NoThrow()
    begin
        H.SeedRnd(42);
        Assert.IsTrue(true, 'Randomize(seed) must not throw');
    end;

    [Test]
    procedure Randomize_NoArg_NoThrow()
    begin
        H.SeedRndNoArg();
        Assert.IsTrue(true, 'Randomize() must not throw');
    end;

    // ── Compilation proof ─────────────────────────────────────────────────────

    [Test]
    procedure AllMethods_Compile()
    begin
        Assert.IsTrue(true, 'All System array/random methods must compile');
    end;
}
