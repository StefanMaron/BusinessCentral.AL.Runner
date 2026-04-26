codeunit 61101 "LRR RemoveRange Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure RemoveRange_Middle_LeavesFirstAndLast()
    var
        Helper: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        // Positive: RemoveRange(2, 3) on [1,2,3,4,5] leaves [1,5].
        Result := Helper.BuildAndRemoveRange(2, 3);
        Assert.AreEqual(2, Result.Count(), 'Count must be 2 after removing 3 middle elements');
        Assert.AreEqual(1, Result.Get(1), 'First element must be 1');
        Assert.AreEqual(5, Result.Get(2), 'Second element must be 5');
    end;

    [Test]
    procedure RemoveRange_FromStart_LeavesRemainder()
    var
        Helper: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        // Positive: RemoveRange(1, 2) on [1,2,3,4,5] leaves [3,4,5].
        Result := Helper.BuildAndRemoveRange(1, 2);
        Assert.AreEqual(3, Result.Count(), 'Count must be 3 after removing 2 from start');
        Assert.AreEqual(3, Result.Get(1), 'First element must be 3');
        Assert.AreEqual(4, Result.Get(2), 'Second element must be 4');
        Assert.AreEqual(5, Result.Get(3), 'Third element must be 5');
    end;

    [Test]
    procedure RemoveRange_FromEnd_LeavesHead()
    var
        Helper: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        // Positive: RemoveRange(4, 2) on [1,2,3,4,5] leaves [1,2,3].
        Result := Helper.BuildAndRemoveRange(4, 2);
        Assert.AreEqual(3, Result.Count(), 'Count must be 3 after removing 2 from end');
        Assert.AreEqual(1, Result.Get(1), 'First element must be 1');
        Assert.AreEqual(2, Result.Get(2), 'Second element must be 2');
        Assert.AreEqual(3, Result.Get(3), 'Third element must be 3');
    end;

    [Test]
    procedure RemoveRange_SingleElement_EmptiesList()
    var
        Helper: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        // Edge case: removing the only element leaves an empty list.
        Result := Helper.RemoveOnlyElement();
        Assert.AreEqual(0, Result.Count(), 'List must be empty after removing only element');
    end;

    [Test]
    procedure RemoveRange_ResultNotOriginalSize()
    var
        Helper: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        // Negative: after RemoveRange(2, 3) the count must NOT be 5.
        Result := Helper.BuildAndRemoveRange(2, 3);
        Assert.AreNotEqual(5, Result.Count(), 'Count must not be 5 after removal');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "LRR Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "LRR Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
