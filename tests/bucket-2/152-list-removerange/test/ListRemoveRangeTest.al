codeunit 61101 "LRR Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: correct elements removed, correct count remains.
    // ------------------------------------------------------------------

    [Test]
    procedure RemoveRange_MiddleElements_CorrectCountRemains()
    var
        H: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        H.BuildAndRemoveMiddle(Result);
        Assert.AreEqual(2, Result.Count(), 'After RemoveRange(2,3) from 5-element list, count must be 2');
    end;

    [Test]
    procedure RemoveRange_MiddleElements_FirstElementPreserved()
    var
        H: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        H.BuildAndRemoveMiddle(Result);
        Assert.AreEqual(1, Result.Get(1), 'First element must be 1 (was before removed range)');
    end;

    [Test]
    procedure RemoveRange_MiddleElements_LastElementPreserved()
    var
        H: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        H.BuildAndRemoveMiddle(Result);
        Assert.AreEqual(5, Result.Get(2), 'Second element must be 5 (was after removed range)');
    end;

    [Test]
    procedure RemoveRange_SingleFirst_CorrectCountRemains()
    var
        H: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        H.BuildAndRemoveFirst(Result);
        Assert.AreEqual(2, Result.Count(), 'After RemoveRange(1,1) from 3-element list, count must be 2');
    end;

    [Test]
    procedure RemoveRange_SingleFirst_HeadBecomesSecond()
    var
        H: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        H.BuildAndRemoveFirst(Result);
        Assert.AreEqual(20, Result.Get(1), 'After removing first, new head must be 20');
    end;

    [Test]
    procedure RemoveRange_SingleFirst_TailPreserved()
    var
        H: Codeunit "LRR Helper";
        Result: List of [Integer];
    begin
        H.BuildAndRemoveFirst(Result);
        Assert.AreEqual(30, Result.Get(2), 'Tail element 30 must be preserved');
    end;

    [Test]
    procedure RemoveRange_AllElements_CountIsZero()
    var
        H: Codeunit "LRR Helper";
    begin
        Assert.AreEqual(0, H.RemoveAllReturnCount(), 'After removing all elements, count must be 0');
    end;

    // ------------------------------------------------------------------
    // Negative: out-of-range index raises an error.
    // ------------------------------------------------------------------

    [Test]
    procedure RemoveRange_OutOfBoundsIndex_RaisesError()
    var
        H: Codeunit "LRR Helper";
    begin
        asserterror H.RemoveOutOfRange();
        Assert.IsTrue(StrLen(GetLastErrorText()) > 0, 'Out-of-bounds RemoveRange must raise an error');
    end;
}
