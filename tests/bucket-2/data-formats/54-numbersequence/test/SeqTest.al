codeunit 56541 "NS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ExistsInsertNoThrow()
    var
        Probe: Codeunit "NS Probe";
    begin
        // [GIVEN] A sequence name that may or may not exist
        // [THEN] Exists/Insert must not throw; the procedure returns its sentinel
        Assert.AreEqual(1, Probe.ProbeExistsThenInsert('ProbeSeq'), 'Exists/Insert must not throw');
    end;

    [Test]
    procedure NextReturnsBigInteger()
    var
        Probe: Codeunit "NS Probe";
        V: BigInteger;
    begin
        V := Probe.ProbeNext('ProbeSeq2');
        Assert.IsTrue(V >= 0, 'Next must return a non-negative BigInteger');
    end;
}
