codeunit 50260 "Test Report Triggers"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure OnPreReport_IsFired()
    var
        Log: Record "Trigger Log Table";
        Rpt: Report "Report With Triggers";
    begin
        Log.DeleteAll();
        Rpt.Run();
        Log.SetRange(TriggerName, 'PRE');
        Assert.IsTrue(Log.FindFirst(), 'OnPreReport must fire when the report runs');
    end;

    [Test]
    procedure OnPostReport_IsFired()
    var
        Log: Record "Trigger Log Table";
        Rpt: Report "Report With Triggers";
    begin
        Log.DeleteAll();
        Rpt.Run();
        Log.SetRange(TriggerName, 'POST');
        Assert.IsTrue(Log.FindFirst(), 'OnPostReport must fire when the report runs');
    end;

    [Test]
    procedure OnPreReport_FiresBeforeOnPostReport()
    var
        Log: Record "Trigger Log Table";
        Rpt: Report "Report With Triggers";
        PreLineNo: Integer;
        PostLineNo: Integer;
    begin
        Log.DeleteAll();
        Rpt.Run();

        Log.SetRange(TriggerName, 'PRE');
        Log.FindFirst();
        PreLineNo := Log."Line No.";

        Log.SetRange(TriggerName, 'POST');
        Log.FindFirst();
        PostLineNo := Log."Line No.";

        Assert.IsTrue(PreLineNo < PostLineNo, 'OnPreReport must fire before OnPostReport');
    end;
}
