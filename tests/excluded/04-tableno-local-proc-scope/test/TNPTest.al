codeunit 310010 "TNP Scope Tests"
{
    Subtype = Test;

    [Test]
    procedure LocalProc_SameNameAsRecMethod_CallsLocal()
    var
        TestRec: Record "TNP Test Record";
        GetStatusCU: Codeunit "TNP Get Status";
    begin
        // [GIVEN] A record exists with empty status
        TestRec.DeleteAll();
        TestRec.Id := 1;
        TestRec.Status := '';
        TestRec.Insert();

        // [WHEN] Running the codeunit (OnRun calls GetStatus(Rec) — should call local procedure)
        GetStatusCU.Run(TestRec);

        // [THEN] The local procedure was called (sets 'FromLocal'), not the record method (sets 'FromRecord')
        TestRec.Get(1);
        Assert.AreEqual('FromLocal', TestRec.Status,
            'GetStatus(Rec) in OnRun must call local procedure, not record method via implicit with');
    end;

    var
        Assert: Codeunit Assert;
}
