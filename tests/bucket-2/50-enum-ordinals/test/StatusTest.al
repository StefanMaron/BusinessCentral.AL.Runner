codeunit 56501 "EO Status Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

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
}
