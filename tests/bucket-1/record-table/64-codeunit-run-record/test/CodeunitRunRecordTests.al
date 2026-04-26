codeunit 57903 "CRR Codeunit Run Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Codeunit.Run(ID, var Rec) — return value
    // -----------------------------------------------------------------------

    [Test]
    procedure CodeunitRun_WithRecord_ReturnsTrueOnSuccess()
    var
        Rec: Record "CRR Order";
        Success: Boolean;
    begin
        // [GIVEN] A record inserted into the store
        Rec.DeleteAll();
        Rec.Init();
        Rec."No." := 'ORD001';
        Rec.Status := 'NEW';
        Rec.Amount := 100;
        Rec.Insert();

        // [WHEN] Codeunit.Run called with a valid record
        Success := Codeunit.Run(Codeunit::"CRR Process Order", Rec);

        // [THEN] Returns true — the codeunit ran without error
        Assert.IsTrue(Success, 'Codeunit.Run must return true when OnRun completes without error');
    end;

    [Test]
    procedure CodeunitRun_WithRecord_ReturnsFalseOnError()
    var
        Rec: Record "CRR Order";
        Success: Boolean;
    begin
        // [GIVEN] A record (content irrelevant — the codeunit always errors)
        Rec.DeleteAll();
        Rec.Init();
        Rec."No." := 'ORD002';
        Rec.Insert();

        // [WHEN] Codeunit.Run called with a codeunit that raises an error
        Success := Codeunit.Run(Codeunit::"CRR Failing Order", Rec);

        // [THEN] Returns false — error was suppressed
        Assert.IsFalse(Success, 'Codeunit.Run must return false when OnRun raises an error');
    end;

    // -----------------------------------------------------------------------
    // Codeunit.Run(ID, var Rec) — OnRun modifications visible after run
    // -----------------------------------------------------------------------

    [Test]
    procedure CodeunitRun_WithRecord_ModificationsVisibleAfterRun()
    var
        Rec: Record "CRR Order";
        ReloaD: Record "CRR Order";
    begin
        // [GIVEN] A record with Status='NEW' and Amount=50
        Rec.DeleteAll();
        Rec.Init();
        Rec."No." := 'ORD010';
        Rec.Status := 'NEW';
        Rec.Amount := 50;
        Rec.Insert();

        // [WHEN] Codeunit.Run with CRR Process Order (sets Status='DONE', doubles Amount)
        Codeunit.Run(Codeunit::"CRR Process Order", Rec);

        // [THEN] Changes are stored — reloading from table confirms them
        ReloaD.Get('ORD010');
        Assert.AreEqual('DONE', ReloaD.Status, 'Status must be updated by OnRun and persisted');
        Assert.AreEqual(100, ReloaD.Amount, 'Amount must be doubled by OnRun and persisted');
    end;

    [Test]
    procedure CodeunitRun_WithRecord_InMemoryRecReflectsChanges()
    var
        Rec: Record "CRR Order";
    begin
        // [GIVEN] A record with Amount=25
        Rec.DeleteAll();
        Rec.Init();
        Rec."No." := 'ORD020';
        Rec.Status := 'OPEN';
        Rec.Amount := 25;
        Rec.Insert();

        // [WHEN] Codeunit.Run executed
        Codeunit.Run(Codeunit::"CRR Process Order", Rec);

        // [THEN] The in-memory Rec variable itself reflects the new values
        Assert.AreEqual('DONE', Rec.Status, 'In-memory Rec.Status must reflect OnRun change');
        Assert.AreEqual(50, Rec.Amount, 'In-memory Rec.Amount must reflect OnRun change');
    end;

    // -----------------------------------------------------------------------
    // Codeunit.Run — negative: error propagates when not captured
    // -----------------------------------------------------------------------

    [Test]
    procedure CodeunitRun_WithRecord_ErrorPropagatesWhenNotCaptured()
    var
        Rec: Record "CRR Order";
    begin
        // [GIVEN] A record
        Rec.DeleteAll();
        Rec.Init();
        Rec."No." := 'ORD030';
        Rec.Insert();

        // [WHEN] Codeunit.Run called and return value not captured
        // [THEN] Error raised by the codeunit propagates to caller
        asserterror Codeunit.Run(Codeunit::"CRR Failing Order", Rec);
        Assert.ExpectedError('deliberate failure');
    end;
}
