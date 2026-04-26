codeunit 108002 "Trigger Recursion Test"
{
    Subtype = Test;

    [Test]
    procedure Modify_WithRecursiveTrigger_DoesNotStackOverflow()
    var
        Rec: Record "Recursive Trigger Table";
        Assert: Codeunit "Library Assert";
    begin
        // [GIVEN] A record in a table with a recursive OnModify trigger
        Rec.PK := 'TEST';
        Rec.Counter := 0;
        Rec.Insert(false);

        // [WHEN] Modify with runTrigger = true
        // The OnModify trigger increments Counter and calls Modify(true) again.
        // Without the recursion guard, this would StackOverflow.
        Rec.Modify(true);

        // [THEN] Counter was incremented once (trigger ran once, recursion blocked)
        Rec.Get('TEST');
        Assert.AreEqual(1, Rec.Counter, 'Counter should be 1 — trigger ran once, recursion was blocked');
    end;
}
