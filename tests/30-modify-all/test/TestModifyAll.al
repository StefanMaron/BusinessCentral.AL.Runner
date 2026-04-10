codeunit 50110 "Test Modify All"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ModifyAllUpdatesAllRecords()
    var
        Rec: Record "Mod All Table";
    begin
        // Positive: ModifyAll updates all records in the table
        Rec.Init();
        Rec."No." := 'A';
        Rec."Status" := 'Draft';
        Rec.Insert(true);

        Rec.Init();
        Rec."No." := 'B';
        Rec."Status" := 'Draft';
        Rec.Insert(true);

        Rec.Init();
        Rec."No." := 'C';
        Rec."Status" := 'Draft';
        Rec.Insert(true);

        Rec.ModifyAll("Status", 'Done');

        Rec.Get('A');
        Assert.AreEqual('Done', Rec."Status", 'Record A should be Done');
        Rec.Get('B');
        Assert.AreEqual('Done', Rec."Status", 'Record B should be Done');
        Rec.Get('C');
        Assert.AreEqual('Done', Rec."Status", 'Record C should be Done');
    end;

    [Test]
    procedure ModifyAllWithFilterUpdatesMatchingOnly()
    var
        Rec: Record "Mod All Table";
    begin
        // Positive: ModifyAll with filter only updates matching records
        Rec.Init();
        Rec."No." := 'X';
        Rec."Name" := 'Alpha';
        Rec."Amount" := 10;
        Rec.Insert(true);

        Rec.Init();
        Rec."No." := 'Y';
        Rec."Name" := 'Beta';
        Rec."Amount" := 20;
        Rec.Insert(true);

        Rec.Init();
        Rec."No." := 'Z';
        Rec."Name" := 'Alpha';
        Rec."Amount" := 30;
        Rec.Insert(true);

        Rec.SetRange("Name", 'Alpha');
        Rec.ModifyAll("Amount", 99);

        Rec.Reset();
        Rec.Get('X');
        Assert.AreEqual(99, Rec."Amount", 'Record X (Alpha) should be 99');
        Rec.Get('Y');
        Assert.AreEqual(20, Rec."Amount", 'Record Y (Beta) should still be 20');
        Rec.Get('Z');
        Assert.AreEqual(99, Rec."Amount", 'Record Z (Alpha) should be 99');
    end;

    [Test]
    procedure ModifyAllOnEmptyTableDoesNotCrash()
    var
        Rec: Record "Mod All Table";
    begin
        // Negative: ModifyAll on empty table is a no-op
        Rec.ModifyAll("Status", 'Done');
        Assert.AreEqual(0, Rec.Count(), 'Table should still be empty');
    end;

    [Test]
    procedure ModifyAllWithRunTrigger()
    var
        Rec: Record "Mod All Table";
    begin
        // Positive: ModifyAll with runTrigger parameter works
        Rec.Init();
        Rec."No." := 'T1';
        Rec."Status" := 'Old';
        Rec.Insert(true);

        Rec.ModifyAll("Status", 'New', true);

        Rec.Get('T1');
        Assert.AreEqual('New', Rec."Status", 'Record should be New after ModifyAll with trigger');
    end;
}
