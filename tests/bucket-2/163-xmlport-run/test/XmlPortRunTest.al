codeunit 61912 "XR XmlPort Run Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure XmlPortRun_NoError()
    var
        Helper: Codeunit "XR Helper";
    begin
        // Positive: XmlPort.Run must not throw in standalone mode (no-op stub).
        Helper.RunXmlPort();
        Assert.IsTrue(true, 'XmlPort.Run must complete without error');
    end;

    [Test]
    procedure XmlPortRun_CalledTwice_NoError()
    var
        Helper: Codeunit "XR Helper";
    begin
        // Edge case: calling XmlPort.Run twice must not error.
        Helper.RunXmlPort();
        Helper.RunXmlPort();
        Assert.IsTrue(true, 'XmlPort.Run called twice must not error');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "XR Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "XR Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
