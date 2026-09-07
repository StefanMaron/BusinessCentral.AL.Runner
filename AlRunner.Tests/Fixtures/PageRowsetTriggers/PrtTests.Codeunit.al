codeunit 70645 "PRT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "PRT Assert";
        Trace: Codeunit "PRT Trace";

    local procedure Seed()
    var
        Row: Record "PRT Row";
        I: Integer;
    begin
        Row.Reset();
        Row.DeleteAll();
        for I := 1 to 8 do begin
            Row.Init();
            Row."No." := 'L' + PadStr('', 4 - StrLen(Format(I)), '0') + Format(I);
            Row.Insert();
        end;
        Trace.Reset();
    end;

    [Test]
    procedure TriggerPage_FirstAndNext_WalkTheRowsetTheTriggersServe()
    var
        List: TestPage "PRT List";
    begin
        // The buffer holds rows 1, 3, 5 and 7; nothing in the table distinguishes them, so no
        // filter could produce this walk. The table's own descending first row is 'L0008'.
        Seed();
        List.OpenView();
        List.FillTemp.Invoke();

        Assert.IsTrue(List.First(), 'First()');
        Assert.AreEqual('L0007', List."No.".Value, 'row 1 of the trigger rowset');
        Assert.IsTrue(List.Next(), 'Next() to row 2');
        Assert.AreEqual('L0005', List."No.".Value, 'row 2 of the trigger rowset');
        Assert.IsTrue(List.Next(), 'Next() to row 3');
        Assert.AreEqual('L0003', List."No.".Value, 'row 3 of the trigger rowset');
        Assert.IsTrue(List.Next(), 'Next() to row 4');
        Assert.AreEqual('L0001', List."No.".Value, 'row 4 of the trigger rowset');
        Assert.IsFalse(List.Next(), 'Next() past the last row the triggers serve');
        List.Close();
    end;

    [Test]
    procedure TriggerPage_LastAndPrevious_WalkTheRowsetTheTriggersServe()
    var
        List: TestPage "PRT List";
    begin
        // Last() lands on 'L0001', which the table would also answer, so the assertion carrying
        // this test is the Previous() step: 'L0003' from the buffer against 'L0002' from the
        // table.
        Seed();
        List.OpenView();
        List.FillTemp.Invoke();

        Assert.IsTrue(List.Last(), 'Last()');
        Assert.AreEqual('L0001', List."No.".Value, 'the last row the triggers serve');
        Assert.IsTrue(List.Previous(), 'Previous()');
        Assert.AreEqual('L0003', List."No.".Value, 'the row before the last one');
        List.Close();
    end;

    [Test]
    procedure TriggerPage_GoToKey_RefusesARowTheTriggersDoNotServe()
    var
        List: TestPage "PRT List";
    begin
        // 'L0004' is in the table and not in the buffer. This is the arm an implementation that
        // fell back to the table would answer true to while every positive arm still passed.
        Seed();
        List.OpenView();
        List.FillTemp.Invoke();

        Assert.IsTrue(List.GoToKey('L0003'), 'GoToKey on a row the triggers serve');
        Assert.AreEqual('L0003', List."No.".Value, 'the row GoToKey landed on');
        Assert.IsFalse(List.GoToKey('L0004'), 'GoToKey on a table row the triggers do not serve');
        List.Close();
    end;

    [Test]
    procedure TriggerPage_FirstAndNext_PassMinusAndPlusOne()
    var
        List: TestPage "PRT List";
    begin
        // The runner's OWN call sequence, which the corpus deliberately does not pin: First()
        // is OnFindRecord('-'), Next() is OnNextRecord(1), Previous() is OnNextRecord(-1) and
        // Last() is OnFindRecord('+'). A page's trigger forwards these to Find/Next, so these
        // four values are the ones any documented implementation of the shape answers
        // identically -- see docs/page-rowset-triggers.md.
        Seed();
        List.OpenView();
        List.FillTemp.Invoke();
        Trace.Reset();

        List.First();
        Assert.AreEqual('F[-]', Trace.Get(), 'First() raises OnFindRecord(''-'')');

        Trace.Reset();
        List.Next();
        Assert.AreEqual('N[1]', Trace.Get(), 'Next() raises OnNextRecord(1)');

        Trace.Reset();
        List.Previous();
        Assert.AreEqual('N[-1]', Trace.Get(), 'Previous() raises OnNextRecord(-1)');

        Trace.Reset();
        List.Last();
        Assert.AreEqual('F[+]', Trace.Get(), 'Last() raises OnFindRecord(''+'')');
        List.Close();
    end;

    [Test]
    procedure TriggerPage_BeforeTheBufferIsActive_TheTriggersStillDecide()
    var
        List: TestPage "PRT List";
    begin
        // The same page before the action runs: its triggers take their `not RunOnTemp` arm and
        // hand Which/Steps to Rec. The rows are the table's, and the trace proves they got
        // there THROUGH the page rather than around it -- which is the distinction the row
        // values alone cannot make.
        Seed();
        List.OpenView();
        Trace.Reset();

        Assert.IsTrue(List.First(), 'First()');
        Assert.AreEqual('L0008', List."No.".Value, 'the table''s own first row, descending');
        Assert.IsTrue(List.Next(), 'Next()');
        Assert.AreEqual('L0007', List."No.".Value, 'the table''s own second row, descending');
        Assert.AreEqual('F[-]N[1]', Trace.Get(), 'both navigations went through the page');
        List.Close();
    end;

    [Test]
    procedure PlainPage_DeclaringNeitherTrigger_RaisesNeither()
    var
        Plain: TestPage "PRT Plain List";
    begin
        // The negative direction, and the reason the declaration check has to be DeclaredOnly:
        // OnFindRecord and OnNextRecord are virtuals whose BASE bodies are the platform find and
        // the platform step, so a check that merely resolved the method would fire on every page
        // in existence. An empty trace after a full walk is what says it did not.
        Seed();
        Plain.OpenView();
        Trace.Reset();

        Assert.IsTrue(Plain.First(), 'First() over the table');
        Assert.AreEqual('L0008', Plain."No.".Value, 'the table''s own first row, descending');
        Assert.IsTrue(Plain.Next(), 'Next() over the table');
        Assert.AreEqual('L0007', Plain."No.".Value, 'the table''s own second row, descending');
        Assert.IsTrue(Plain.Last(), 'Last() over the table');
        Assert.AreEqual('L0001', Plain."No.".Value, 'the table''s own last row, descending');

        Assert.AreEqual('', Trace.Get(), 'a page declaring neither trigger raises neither');
        Plain.Close();
    end;
}
