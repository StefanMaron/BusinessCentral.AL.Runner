codeunit 56501 "EO Status Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Enum.Ordinals — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure OrdinalsReturnsFourValues()
    var
        Inspector: Codeunit "EO Inspector";
    begin
        // [GIVEN] An enum with 4 declared values (including the empty one)
        // [THEN] Ordinals() returns a list whose Count is 4
        Assert.AreEqual(4, Inspector.CountOrdinals(), 'EO Status has four declared members');
    end;

    [Test]
    procedure FirstOrdinalIsZero()
    var
        Inspector: Codeunit "EO Inspector";
    begin
        // First declared ordinal is 0 (the empty member)
        Assert.AreEqual(0, Inspector.FirstOrdinal(), 'First ordinal should be 0');
    end;

    [Test]
    procedure SecondOrdinalIsOne()
    var
        Inspector: Codeunit "EO Inspector";
    begin
        // [GIVEN] EO Status has: " "(0), Open(1), Closed(2), Archived(3)
        // [THEN] Second ordinal is 1 (Open)
        Assert.AreEqual(1, Inspector.SecondOrdinal(), 'Second ordinal should be 1 (Open)');
    end;

    [Test]
    procedure ThirdOrdinalIsTwo()
    var
        Inspector: Codeunit "EO Inspector";
    begin
        Assert.AreEqual(2, Inspector.ThirdOrdinal(), 'Third ordinal should be 2 (Closed)');
    end;

    [Test]
    procedure FourthOrdinalIsThree()
    var
        Inspector: Codeunit "EO Inspector";
    begin
        Assert.AreEqual(3, Inspector.FourthOrdinal(), 'Fourth ordinal should be 3 (Archived)');
    end;

    [Test]
    procedure OrdinalsContainsTwo()
    var
        Inspector: Codeunit "EO Inspector";
    begin
        // [GIVEN] Closed has ordinal 2
        // [THEN] Ordinals().Contains(2) is true
        Assert.IsTrue(Inspector.OrdinalsContains(2), 'Ordinals should contain 2 (Closed)');
    end;

    [Test]
    procedure OrdinalsInstanceSyntaxReturnsSameCount()
    var
        Inspector: Codeunit "EO Inspector";
    begin
        // Verify that instance-variable syntax E.Ordinals() gives same result
        Assert.AreEqual(4, Inspector.OrdinalsInstanceSyntaxCount(), 'Instance syntax should also return 4 ordinals');
    end;

    // -----------------------------------------------------------------------
    // Enum.Ordinals — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure OrdinalsDoesNotContainNine()
    var
        Inspector: Codeunit "EO Inspector";
    begin
        // [GIVEN] 9 is not a declared ordinal of EO Status
        // [THEN] Ordinals().Contains(9) returns false
        Assert.IsFalse(Inspector.OrdinalsContains(9), 'Ordinals should not contain 9');
    end;

    [Test]
    procedure OrdinalsCountIsNotThree()
    var
        Inspector: Codeunit "EO Inspector";
    begin
        // A no-op returning a short list would fail this
        Assert.AreNotEqual(3, Inspector.CountOrdinals(), 'Count must not be 3 — EO Status has exactly 4 members');
    end;
}
