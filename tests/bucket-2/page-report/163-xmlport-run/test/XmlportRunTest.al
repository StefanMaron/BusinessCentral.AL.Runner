codeunit 59761 "XPR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XPR Src";

    [Test]
    procedure XmlportRun_SingleArg_NoOp()
    begin
        // Positive: the standalone stub must execute without error. There is no
        // file I/O or interactive import/export target, so Xmlport.Run is a
        // no-op that completes and lets execution continue.
        Src.CallRun();
        Assert.IsTrue(true, 'Xmlport.Run must not throw');
    end;

    [Test]
    procedure XmlportRun_ExecutionContinues()
    begin
        // Proving: execution continues past Xmlport.Run (flag gets set).
        Assert.IsTrue(Src.CallRunAndReturnFlag(),
            'Caller must reach `exit(true)` after Xmlport.Run');
    end;

    [Test]
    procedure XmlportRun_WithShowPageFalse()
    begin
        // 3-arg overload with showPage=false / showXml=false must complete.
        Assert.IsTrue(Src.CallRunWithShowPage(false, false),
            'Xmlport.Run 3-arg (false, false) must complete');
    end;

    [Test]
    procedure XmlportRun_WithShowPageTrue()
    begin
        // 3-arg overload with both showPage=true / showXml=true must also complete
        // (no UI standalone; the flags are ignored).
        Assert.IsTrue(Src.CallRunWithShowPage(true, true),
            'Xmlport.Run 3-arg (true, true) must complete');
    end;

    [Test]
    procedure XmlportRun_MixedFlags_NegativeTrap()
    begin
        // Negative trap: flag interaction must not throw. Tests both mixed
        // combinations that a naive stub might accidentally branch on.
        Assert.IsTrue(Src.CallRunWithShowPage(true, false),
            'Xmlport.Run (true, false) must complete');
        Assert.IsTrue(Src.CallRunWithShowPage(false, true),
            'Xmlport.Run (false, true) must complete');
    end;
}
