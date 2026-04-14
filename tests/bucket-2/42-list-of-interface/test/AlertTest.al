codeunit 50432 "Alert Dispatch Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure DispatchSumsAllHandlers()
    var
        Holder: Record "Alert Holder";
    begin
        // [GIVEN] A record that holds two interface implementations
        // [WHEN] Dispatching via `List of [Interface IMyAlert]`
        // [THEN] Each handler runs and the results are summed
        Assert.AreEqual(11, Holder.Dispatch(), 'Dispatch should sum Low(1) + High(10)');
    end;

    [Test]
    procedure HolderRecordIsStillUsable()
    var
        Holder: Record "Alert Holder";
    begin
        // [GIVEN] The table has a procedure that contains a List of [Interface X]
        // [THEN] The record itself must still insert/read correctly — i.e. the
        //        containing table is not excluded from compilation.
        Holder.Id := 1;
        Holder.Insert();
        Assert.AreEqual(1, Holder.Count(), 'Alert Holder table must be usable despite containing List of [Interface]');
    end;
}
