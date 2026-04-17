/// Tests for TestPage custom action OnAction dispatch (issue #832).
/// TP.MyAction.Invoke() must execute the action's OnAction trigger.
codeunit 109001 "TAI Test"
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    // ── SetFlag action ────────────────────────────────────────────────────────

    [Test]
    procedure SetFlag_Action_SetsFlag()
    var
        Rec: Record "TAI Table";
        TP: TestPage "TAI Card";
    begin
        // [GIVEN] A record with Flag=false
        Rec."No." := 'F1';
        Rec.Flag := false;
        Rec.Insert();

        // [WHEN] SetFlag.Invoke() is called on the TestPage
        TP.OpenEdit();
        TP.GoToRecord(Rec);
        TP.SetFlag.Invoke();

        // [THEN] Flag is set to true
        Rec.Find();
        Assert.IsTrue(Rec.Flag, 'SetFlag action must set Flag to true');
    end;

    // ── IncrementCounter action ───────────────────────────────────────────────

    [Test]
    procedure IncrementCounter_Action_IncrementsCounter()
    var
        Rec: Record "TAI Table";
        TP: TestPage "TAI Card";
    begin
        // [GIVEN] A record with Counter=5
        Rec."No." := 'C1';
        Rec.Counter := 5;
        Rec.Insert();

        // [WHEN] IncrementCounter.Invoke() is called
        TP.OpenEdit();
        TP.GoToRecord(Rec);
        TP.IncrementCounter.Invoke();

        // [THEN] Counter is incremented to 6
        Rec.Find();
        Assert.AreEqual(6, Rec.Counter, 'IncrementCounter action must increment Counter from 5 to 6');
    end;

    // ── Two actions on same record ────────────────────────────────────────────

    [Test]
    procedure TwoActions_BothFireCorrectly()
    var
        Rec: Record "TAI Table";
        TP: TestPage "TAI Card";
    begin
        // [GIVEN] A record with Flag=false and Counter=0
        Rec."No." := 'B1';
        Rec.Flag := false;
        Rec.Counter := 0;
        Rec.Insert();

        // [WHEN] Both actions are invoked in sequence
        TP.OpenEdit();
        TP.GoToRecord(Rec);
        TP.SetFlag.Invoke();
        TP.IncrementCounter.Invoke();

        // [THEN] Both side effects are applied
        Rec.Find();
        Assert.IsTrue(Rec.Flag, 'SetFlag must have fired');
        Assert.AreEqual(1, Rec.Counter, 'IncrementCounter must have fired');
    end;
}
