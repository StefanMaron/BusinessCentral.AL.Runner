codeunit 83601 "EIT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EIT Src";

    [Test]
    procedure Title_SetAndGet_RoundsTrip()
    begin
        // Positive: Title getter returns what the setter set.
        Assert.AreEqual(
            'Validation Error',
            Src.SetAndGet('Validation Error'),
            'Title must round-trip the set value');
    end;

    [Test]
    procedure Title_EmptyString_RoundsTrip()
    begin
        // Edge: empty string round-trips cleanly.
        Assert.AreEqual('', Src.SetAndGet(''),
            'Title must round-trip an empty string');
    end;

    [Test]
    procedure Title_Fresh_IsEmpty()
    begin
        // Positive: a default-initialised ErrorInfo has an empty Title.
        Assert.AreEqual('', Src.DefaultTitle(),
            'Fresh ErrorInfo must have empty Title');
    end;

    [Test]
    procedure Title_LastWriteWins()
    begin
        // Proving: repeated setter calls overwrite — last value wins.
        Assert.AreEqual(
            'Second Title',
            Src.LastWriteWins('First Title', 'Second Title'),
            'Title must reflect the last setter call');
    end;

    [Test]
    procedure Title_DifferentInputs_DifferentOutputs()
    begin
        // Negative: guard against a stub that returns a fixed value.
        Assert.AreNotEqual(
            Src.SetAndGet('alpha'),
            Src.SetAndGet('beta'),
            'Different inputs must produce different Title values');
    end;
}
