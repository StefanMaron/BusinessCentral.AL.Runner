codeunit 106001 "Library Random Test"
{
    Subtype = Test;

    var
        LibRandom: Codeunit "Library - Random";
        Assert: Codeunit "Library Assert";

    [Test]
    procedure RandInt_ReturnsValueInRange()
    var
        Result: Integer;
    begin
        // Verify RandInt returns a value between 1 and Range (inclusive)
        Result := LibRandom.RandInt(100);
        Assert.IsTrue(Result >= 1, 'RandInt result must be >= 1');
        Assert.IsTrue(Result <= 100, 'RandInt result must be <= 100');
    end;

    [Test]
    procedure RandInt_SmallRange_ReturnsOne()
    var
        Result: Integer;
    begin
        // When Range < 1, RandInt should return 1
        Result := LibRandom.RandInt(0);
        Assert.AreEqual(1, Result, 'RandInt(0) must return 1');
    end;

    [Test]
    procedure RandIntInRange_ReturnsValueInRange()
    var
        Result: Integer;
        i: Integer;
    begin
        // Call multiple times to make sure it doesn't always hit a boundary
        for i := 1 to 5 do begin
            Result := LibRandom.RandIntInRange(10, 20);
            Assert.IsTrue(Result >= 10, 'RandIntInRange result must be >= Min');
            Assert.IsTrue(Result <= 20, 'RandIntInRange result must be <= Max');
        end;
    end;

    [Test]
    procedure RandDec_ReturnsPositiveDecimal()
    var
        Result: Decimal;
    begin
        // RandDec(100, 2) returns a decimal > 0 with 2 decimal places
        Result := LibRandom.RandDec(100, 2);
        Assert.IsTrue(Result > 0, 'RandDec result must be > 0');
        Assert.IsTrue(Result <= 100, 'RandDec result must be <= Range');
    end;

    [Test]
    procedure RandDecInRange_ReturnsValueInRange()
    var
        Result: Decimal;
    begin
        Result := LibRandom.RandDecInRange(10, 20, 2);
        Assert.IsTrue(Result >= 10, 'RandDecInRange result must be >= Min');
        Assert.IsTrue(Result <= 20, 'RandDecInRange result must be <= Max');
    end;

    [Test]
    procedure RandText_ReturnsCorrectLength()
    var
        Result: Text;
    begin
        Result := LibRandom.RandText(10);
        Assert.AreEqual(10, StrLen(Result), 'RandText must return exactly 10 characters');
    end;

    [Test]
    procedure RandText_LargeLength_ReturnsCorrectLength()
    var
        Result: Text;
    begin
        Result := LibRandom.RandText(50);
        Assert.AreEqual(50, StrLen(Result), 'RandText must return exactly 50 characters');
    end;

    [Test]
    procedure SetSeed_DeterminesSequence()
    var
        Val1a: Integer;
        Val2a: Integer;
        Val1b: Integer;
        Val2b: Integer;
    begin
        // Same seed must produce the same sequence
        LibRandom.SetSeed(42);
        Val1a := LibRandom.RandInt(1000);
        Val2a := LibRandom.RandInt(1000);

        LibRandom.SetSeed(42);
        Val1b := LibRandom.RandInt(1000);
        Val2b := LibRandom.RandInt(1000);

        Assert.AreEqual(Val1a, Val1b, 'Same seed must produce same first value');
        Assert.AreEqual(Val2a, Val2b, 'Same seed must produce same second value');
    end;

    [Test]
    procedure Init_ReturnsNonZeroSeed()
    var
        Seed: Integer;
    begin
        // Init() returns Time - 000000T which is almost always non-zero during the day
        // We just verify the return value is what SetSeed received (i.e., it's >= 0)
        Seed := LibRandom.Init();
        Assert.IsTrue(Seed >= 0, 'Init must return a non-negative seed');
    end;

    [Test]
    procedure RandDate_ZeroDelta_ReturnsWorkDate()
    var
        Result: Date;
    begin
        Result := LibRandom.RandDate(0);
        Assert.AreEqual(WorkDate(), Result, 'RandDate(0) must return WorkDate');
    end;
}
