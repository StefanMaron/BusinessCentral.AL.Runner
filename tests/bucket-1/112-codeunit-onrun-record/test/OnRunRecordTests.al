codeunit 80900 "OnRun Record Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestOnRunReceivesRecord()
    var
        Rec: Record "OnRun Test Table";
    begin
        // [GIVEN] A record exists
        Rec.Init();
        Rec."No." := 'R001';
        Rec.Description := 'original';
        Rec.Insert();

        // [WHEN] Codeunit.Run is called with the record
        Codeunit.Run(Codeunit::"OnRun Processor", Rec);

        // [THEN] The codeunit received the record and modified it
        Rec.Get('R001');
        Assert.AreEqual('processed', Rec.Description, 'Description should be set by OnRun');
        Assert.AreEqual('RUNNER', Rec."Processed By", 'ProcessedBy should be set by OnRun');
    end;

    [Test]
    procedure TestOnRunModificationVisibleToCaller()
    var
        Rec: Record "OnRun Test Table";
    begin
        // [GIVEN] A record exists with Counter = 5
        Rec.Init();
        Rec."No." := 'R002';
        Rec.Counter := 5;
        Rec.Insert();

        // [WHEN] Codeunit.Run is called — codeunit increments counter by 10
        Codeunit.Run(Codeunit::"OnRun Counter", Rec);

        // [THEN] Counter was modified (persisted to table)
        Rec.Get('R002');
        Assert.AreEqual(15, Rec.Counter, 'Counter should be incremented to 15 by OnRun');
    end;

    [Test]
    procedure TestOnRunWithNonExistingRecord()
    var
        Rec: Record "OnRun Test Table";
    begin
        // [GIVEN] A record that does NOT exist in the table
        Rec.Init();
        Rec."No." := 'NOTEXIST';
        Rec.Description := 'will fail to modify';

        // [WHEN] Codeunit.Run is called — the codeunit calls Modify() which will fail
        asserterror Codeunit.Run(Codeunit::"OnRun Processor", Rec);

        // [THEN] An error is raised (Modify on non-existing record)
        Assert.ExpectedError('not found');
    end;

    [Test]
    procedure TestCatchableOnRunError()
    var
        Rec: Record "OnRun Test Table";
        Success: Boolean;
    begin
        // [GIVEN] A record that does NOT exist
        Rec.Init();
        Rec."No." := 'MISSING';

        // [WHEN] Codeunit.Run returns false when record not found and error is suppressed
        // (Codeunit.Run returns Boolean; error is trapped)
        Success := Codeunit.Run(Codeunit::"OnRun Processor", Rec);

        // [THEN] Returns false (error suppressed)
        Assert.IsFalse(Success, 'Codeunit.Run should return false when OnRun raises an error');
    end;

    [Test]
    procedure TestParameterlessOnRunStillWorks()
    var
        Pl: Codeunit "OnRun Parameterless";
    begin
        // [GIVEN/WHEN] A codeunit without TableNo is called directly
        Pl.SetValue(42);

        // [THEN] It works normally
        Assert.AreEqual(42, Pl.GetValue(), 'Parameterless codeunit should work normally');
    end;

    [Test]
    procedure TestStartSessionWithRecord()
    var
        Rec: Record "OnRun Test Table";
        SessionId: Integer;
        Success: Boolean;
    begin
        // [GIVEN] A record exists
        Rec.Init();
        Rec."No." := 'R003';
        Rec.Description := 'before-session';
        Rec.Insert();

        // [WHEN] StartSession is called with the record
        Success := Session.StartSession(SessionId, Codeunit::"OnRun Session Processor", CompanyName(), Rec);

        // [THEN] Session started and codeunit processed the record
        Assert.IsTrue(Success, 'StartSession should return true');
        Rec.Get('R003');
        Assert.AreEqual('session-processed', Rec.Description, 'Description should be set by session codeunit');
    end;
}
