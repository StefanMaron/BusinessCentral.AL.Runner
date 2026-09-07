// Issue #3029. A part page's OnNewRecord must run ONCE for one draft-line row.
//
// The runner has two paths that each start a record: EnterNewRowLine, when the cursor lands on
// the implicit new-row line, and PromoteNewRowLineForWrite -> InsertEmptyRow, when a write turns
// that line into a pending insert. On the commonest sequence -- First()/Next() onto the draft
// line, then SetValue -- both run for one row.
//
// The witness is a COUNT, not an assignment. Every existing fixture in this area writes
// `Rec."Set By OnNewRecord" := 'NEWREC'`, which is idempotent and so passes whether the trigger
// fired once or five times. Each assertion here names a concrete integer, so it fails against a
// double firing (2) and against a missing one (0) alike.
codeunit 70646 "ONC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "ONC Assert";

    local procedure Initialize()
    var
        Header: Record "ONC Header";
        Line: Record "ONC Line";
        Log: Record "ONC Log";
    begin
        Log.DeleteAll();
        Line.DeleteAll();
        Header.DeleteAll();

        AddHeader('H1');
        AddHeader('H2');
    end;

    local procedure AddHeader(No: Code[20])
    var
        Header: Record "ONC Header";
    begin
        Header.Init();
        Header."No." := No;
        Header.Insert();
    end;

    // Assignment, no page: a seeded line must not raise the part's OnNewRecord, so the log
    // counts only what the tests themselves cause.
    local procedure AddLine(HeaderNo: Code[20]; LineNo: Integer; Descr: Text[50])
    var
        Line: Record "ONC Line";
    begin
        Line.Init();
        Line."Header No." := HeaderNo;
        Line."Line No." := LineNo;
        Line.Descr := Descr;
        Line.Insert();
    end;

    local procedure OpenCardOn(HeaderNo: Code[20]; var Card: TestPage "ONC Card")
    var
        Header: Record "ONC Header";
    begin
        Header.Get(HeaderNo);
        Card.OpenEdit();
        Card.GoToRecord(Header);
    end;

    local procedure FiringCount(): Integer
    var
        Log: Record "ONC Log";
    begin
        exit(Log.Count());
    end;

    local procedure LineCountFor(HeaderNo: Code[20]): Integer
    var
        Line: Record "ONC Line";
    begin
        Line.SetRange("Header No.", HeaderNo);
        exit(Line.Count());
    end;

    // THE CONTROL. New() is one new-record step by construction, so a count other than 1 here
    // would mean the fixture cannot count and no other number in this file could be read.
    [Test]
    procedure New_OnEmptyLinkedPart_RaisesOnNewRecordOnce()
    var
        Card: TestPage "ONC Card";
    begin
        Initialize();
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);
        Card.Lines.New();

        Assert.AreEqual(1, FiringCount(),
            'New() on an empty linked part must raise the part page''s OnNewRecord exactly once');

        Card.Lines.Descr.SetValue('typed after New');

        Assert.AreEqual(1, FiringCount(),
            'writing into the row New() started must not raise OnNewRecord again');

        Card.Close();

        Assert.AreEqual(1, FiringCount(), 'closing the card must not raise OnNewRecord again');
        Assert.AreEqual(1, LineCountFor('H1'), 'exactly one line must be written for H1');
        Assert.AreEqual(1, LineCountFor('H2'), 'H2''s own line must be untouched');
    end;

    // Merely showing the draft line. Not zero -- corpus codeunit 60996 measured on all 8 BC legs
    // that the trigger has already run by this point. This says how often.
    [Test]
    procedure DraftLine_ShownAndUntouched_RaisesOnNewRecordOnce()
    var
        Card: TestPage "ONC Card";
    begin
        Initialize();
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);
        Assert.IsFalse(Card.Lines.First(),
            'H1 has no lines, so First() must return false and land on the draft line');

        Assert.AreEqual(1, FiringCount(),
            'landing on the draft line must raise the part page''s OnNewRecord exactly once');

        Card.Close();

        Assert.AreEqual(1, FiringCount(),
            'closing over an untouched draft line must not raise OnNewRecord again');
        Assert.AreEqual(0, LineCountFor('H1'),
            'an untouched draft line must not be written -- so the firing above is not a saved row');
    end;

    // THE DEFECT #3029 REPORTS. Show the draft line, then write one field. Against the arm above
    // this isolates what the WRITE costs: equal counts mean typing only marks an already-started
    // row for saving; a higher count means the promotion starts the record a second time.
    [Test]
    procedure DraftLine_ShownThenWritten_RaisesOnNewRecordOnce()
    var
        Line: Record "ONC Line";
        Card: TestPage "ONC Card";
    begin
        Initialize();
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);
        Assert.IsFalse(Card.Lines.First(),
            'H1 has no lines, so First() must return false and land on the draft line');
        Assert.AreEqual(1, FiringCount(),
            'landing on the draft line must raise OnNewRecord once, before anything is typed');

        Card.Lines.Descr.SetValue('typed into the draft line');

        // THE MEASUREMENT. Read before Close(), so a firing on the way out cannot be mistaken
        // for one the write caused.
        Assert.AreEqual(1, FiringCount(),
            'writing into the draft line must not raise OnNewRecord a second time -- the row was already started when the blank line became current');

        Card.Close();

        Assert.AreEqual(1, FiringCount(),
            'saving the promoted draft line on close must not raise OnNewRecord again');
        Assert.AreEqual(1, LineCountFor('H1'),
            'typing into the draft line must insert exactly one line for H1');
        Assert.AreEqual(1, LineCountFor('H2'), 'H2''s own line must be untouched');

        Line.SetRange("Header No.", 'H1');
        Line.FindFirst();
        Assert.AreEqual('typed into the draft line', Line.Descr,
            'the typed value must reach the backing table -- the count above is for a row really written');
        Assert.AreEqual('H1', Line."Header No.",
            'the promoted row must still carry the SubPageLink''s value: de-duplicating the firing must not cost the link stamping');
    end;

    // The other route onto the draft line: walking off the end of existing data. The 0 assertion
    // on the real row is what stops the counts here being read as "any cursor move fires it".
    [Test]
    procedure DraftLine_ReachedByNextThenWritten_RaisesOnNewRecordOnce()
    var
        Line: Record "ONC Line";
        Card: TestPage "ONC Card";
    begin
        Initialize();
        AddLine('H1', 10000, 'seeded');
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);
        Assert.IsTrue(Card.Lines.First(), 'the part must land on H1''s seeded line');

        Assert.AreEqual(0, FiringCount(),
            'landing on an existing data row must not raise OnNewRecord at all');

        Assert.IsTrue(Card.Lines.Next(), 'Next() past the last data row must land on the draft line');
        Assert.AreEqual(1, FiringCount(),
            'walking onto the draft line must raise OnNewRecord exactly once');

        Card.Lines.Descr.SetValue('typed after walking to the end');

        Assert.AreEqual(1, FiringCount(),
            'writing into a draft line reached by Next() must not raise OnNewRecord a second time');

        Card.Close();

        Assert.AreEqual(1, FiringCount(), 'saving on close must not raise OnNewRecord again');
        Assert.AreEqual(2, LineCountFor('H1'),
            'the promoted draft line must be a second line for H1, alongside the seeded one');

        Line.SetRange("Header No.", 'H1');
        Line.SetRange(Descr, 'typed after walking to the end');
        Assert.AreEqual(1, Line.Count(), 'exactly one line must carry the typed description');
        Line.FindFirst();
        Assert.IsTrue(Line."Line No." > 10000,
            'AutoSplitKey must still number the promoted line past the line already there: de-duplicating the firing must not cost the insert-position capture');
    end;

    // THE NEGATIVE DIRECTION, and what stops every "exactly once" above from being satisfied by
    // an implementation that simply latched after the first firing. Two rows, two firings.
    [Test]
    procedure TwoRowsThroughTheDraftLine_RaiseOnNewRecordTwice()
    var
        Card: TestPage "ONC Card";
    begin
        Initialize();
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);
        Assert.IsFalse(Card.Lines.First(), 'H1 has no lines, so First() must return false');

        Card.Lines.Descr.SetValue('first line');
        Assert.AreEqual(1, FiringCount(), 'the first row must account for exactly one firing');

        Assert.IsTrue(Card.Lines.Next(),
            'Next() must leave the row just written and land on a fresh draft line');
        Card.Lines.Descr.SetValue('second line');

        Assert.AreEqual(2, FiringCount(),
            'a second row started through the draft line must raise OnNewRecord once more -- one firing per row, not one per page');

        Card.Close();

        Assert.AreEqual(2, FiringCount(), 'closing the card must not raise OnNewRecord again');
        Assert.AreEqual(2, LineCountFor('H1'), 'both rows must be written for H1');
        Assert.AreEqual(1, LineCountFor('H2'), 'H2''s own line must be untouched');
    end;
}
