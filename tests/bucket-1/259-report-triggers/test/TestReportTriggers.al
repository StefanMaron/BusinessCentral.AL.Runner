codeunit 50260 "Test Report Triggers"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure OnPreReport_IsFired()
    var
        State: Codeunit "Report Trigger State";
        Rpt: Report "Report With Triggers";
    begin
        State.Reset();
        Rpt.Run();
        Assert.IsTrue(State.WasPreFired(), 'OnPreReport must fire when the report runs');
    end;

    [Test]
    procedure OnPostReport_IsFired()
    var
        State: Codeunit "Report Trigger State";
        Rpt: Report "Report With Triggers";
    begin
        State.Reset();
        Rpt.Run();
        Assert.IsTrue(State.WasPostFired(), 'OnPostReport must fire when the report runs');
    end;

    [Test]
    procedure OnPreReport_FiresBeforeOnPostReport()
    var
        State: Codeunit "Report Trigger State";
        Rpt: Report "Report With Triggers";
    begin
        State.Reset();
        Rpt.Run();
        Assert.IsTrue(State.PreBeforePost(), 'OnPreReport must fire before OnPostReport');
    end;
}
