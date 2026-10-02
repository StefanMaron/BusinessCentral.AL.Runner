/// <summary>
/// #5161: the generated procedure is called by an event subscriber, not by the test. The test
/// raises OnCounted, so it reaches the stub; the test raising OnOther does not.
/// </summary>
codeunit 65205 "Tdd Shape Event Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure RaisingASubscribedEvent_ReachesTheSubscribersStub()
    var
        Publisher: Codeunit "Tdd Shape Publisher";
    begin
        Publisher.RaiseCounted();
    end;

    [Test]
    procedure RaisingAnotherEvent_IsNotAnnotated()
    var
        Publisher: Codeunit "Tdd Shape Publisher";
    begin
        Publisher.RaiseOther();
    end;
}
