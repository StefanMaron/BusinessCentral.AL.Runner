codeunit 58901 "CGS Cuegroup Section Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "CGS KPI Helper";

    // -----------------------------------------------------------------------
    // Positive: pages with cuegroup sections compile; business logic runs
    // -----------------------------------------------------------------------

    [Test]
    procedure CuegroupSection_RoleCenterPageCompiles()
    begin
        // [GIVEN] A compilation unit containing a RoleCenter page with a cuegroup section
        // [WHEN]  Business logic in the same unit is called
        // [THEN]  It executes — cuegroup section does not block compilation
        Assert.AreEqual(50, Helper.CalcKPIScore(10, 10), 'cuegroup: 10 shipped of 20 total = 50% score');
    end;

    [Test]
    procedure CuegroupSection_FactBoxPageCompiles()
    begin
        // [GIVEN] A CardPart page with a cuegroup section in a FactBox
        // [WHEN]  Business logic is called
        // [THEN]  CardPart cuegroup does not block compilation either
        Assert.AreEqual(75, Helper.CalcKPIScore(10, 30), 'cuegroup FactBox: 30 shipped of 40 total = 75% score');
    end;

    [Test]
    procedure CuegroupSection_AllShipped_FullScore()
    begin
        // Positive: 100% shipped gives 100 score
        Assert.AreEqual(100, Helper.CalcKPIScore(0, 50), '100% shipped should give score 100');
    end;

    [Test]
    procedure CuegroupSection_NothingShipped_ZeroScore()
    begin
        // Positive: nothing shipped gives 0 score
        Assert.AreEqual(0, Helper.CalcKPIScore(20, 0), '0 shipped should give score 0');
    end;

    [Test]
    procedure CuegroupSection_ZeroTotal_ReturnsZero()
    begin
        // Edge case: no orders at all
        Assert.AreEqual(0, Helper.CalcKPIScore(0, 0), 'Zero total orders must return 0 without error');
    end;

    [Test]
    procedure CuegroupSection_IsHighActivity_AboveThreshold()
    begin
        // Negative path: prove the second cuegroup page compiled by checking IsHighActivity
        Assert.IsTrue(Helper.IsHighActivity(150), 'Open orders > 100 must be high activity');
    end;

    [Test]
    procedure CuegroupSection_IsHighActivity_BelowThreshold()
    begin
        // Negative: at/below threshold
        Assert.IsFalse(Helper.IsHighActivity(100), 'Exactly 100 open orders is not high activity');
    end;
}
