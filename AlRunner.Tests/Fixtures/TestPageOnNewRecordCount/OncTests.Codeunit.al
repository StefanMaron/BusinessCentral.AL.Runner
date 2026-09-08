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

    // THE ARM THAT DOES NOT REST ON THE OPENING COST, and the one the two service tiers agree
    // with exactly. Every other arm in this file compares against 1, which is the RUNNER's own
    // cost of opening a card over an EMPTY part; the tiers answer 6 (bc-linux, corpus run
    // 34140530877) and 3 (Microsoft Windows container, nightly run 34182689878) for that same
    // step, and which of those is right is under investigation. So the opening cost is not a
    // portable claim and is deliberately not asserted here.
    //
    // What IS portable is this: a part that already HAS rows never renders a blank draft line
    // while opening, so opening it costs ZERO -- both tiers measured 0 -- and walking across its
    // existing rows adds nothing, because standing on a row that is already there is not
    // starting a record. This arm asserts the whole walk as a DELTA of zero from a baseline it
    // captures itself, so it holds whatever the empty-part opening cost turns out to be.
    //
    // Three seeded rows rather than one: a single row cannot distinguish "landing costs zero"
    // from "the first landing is free and every later one is not", which is exactly the shape
    // the defect had.
    [Test]
    procedure ExistingDataRows_WalkedAcross_RaiseOnNewRecordNotAtAll()
    var
        Card: TestPage "ONC Card";
        AfterOpen: Integer;
    begin
        Initialize();
        AddLine('H1', 10000, 'first');
        AddLine('H1', 20000, 'second');
        AddLine('H1', 30000, 'third');
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);

        // Opening over a part that HAS rows lands on real data, not on a draft line. Both
        // service tiers measured exactly 0 here, so this one absolute IS portable.
        AfterOpen := FiringCount();
        Assert.AreEqual(0, AfterOpen,
            'opening a card whose part already has rows must not raise OnNewRecord at all -- the part lands on real data, never on a draft line');

        Assert.IsTrue(Card.Lines.First(), 'the part must land on H1''s first seeded line');
        Assert.AreEqual(AfterOpen, FiringCount(),
            'First() onto an existing data row must add nothing -- standing on a row that is already there is not starting a record');

        Assert.IsTrue(Card.Lines.Next(), 'Next() must reach the second seeded line');
        Assert.AreEqual(AfterOpen, FiringCount(),
            'Next() onto a second existing data row must add nothing');

        Assert.IsTrue(Card.Lines.Next(), 'Next() must reach the third seeded line');
        Assert.AreEqual(AfterOpen, FiringCount(),
            'Next() onto a third existing data row must add nothing -- so this is "every existing row is free", not "the first one is"');

        // THE OTHER DIRECTION IN THE SAME ARM. One more Next() steps off the data and onto the
        // draft line, which DOES start a row: exactly +1 over the same baseline. Without this
        // the zeros above would also be satisfied by an implementation that never fires at all.
        Assert.IsTrue(Card.Lines.Next(), 'Next() past the last data row must land on the draft line');
        Assert.AreEqual(AfterOpen + 1, FiringCount(),
            'stepping off the last data row onto the draft line must raise OnNewRecord exactly once');

        Card.Close();

        Assert.AreEqual(AfterOpen + 1, FiringCount(),
            'closing over an untouched draft line must not raise OnNewRecord again');
        Assert.AreEqual(3, LineCountFor('H1'),
            'walking across rows and stopping on an untouched draft line must not write a fourth row');
        Assert.AreEqual(1, LineCountFor('H2'), 'H2''s own line must be untouched');
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

    // The latch is ONCE PER ROW, and this is the arm that says so. Every other arm here stays
    // within one parent row, so all of them pass with the latch never cleared at all -- "once
    // per page" satisfies them. Only walking onto a draft line, moving the PARENT, and walking
    // onto a second draft line can tell the two apart: the first draft line is abandoned rather
    // than left, so if the latch survives that, the second row silently owes no firing.
    //
    // Written because review found that removing BOTH latch resets left every existing arm
    // green -- the fixture and all four corpus arms. The comment on AbandonNewRowLine claimed
    // this was "the failure a count-based test catches"; it was not, until this arm.
    [Test]
    procedure DraftLineAbandonedByAParentMove_MakesTheNextRowOweItsOwnFiring()
    var
        Card: TestPage "ONC Card";
    begin
        Initialize();
        AddHeader('H3');

        OpenCardOn('H1', Card);
        Assert.IsFalse(Card.Lines.First(), 'H1 has no lines, so First() must return false');
        Assert.AreEqual(1, FiringCount(),
            'landing on H1''s draft line must raise OnNewRecord exactly once');

        // Re-point the part at a different parent WITHOUT writing the draft line. The part
        // abandons that line rather than leaving it, which is the path AbandonNewRowLine owns.
        Card.GoToKey('H3');
        Assert.IsFalse(Card.Lines.First(), 'H3 has no lines either, so First() must return false');

        Assert.AreEqual(2, FiringCount(),
            'the draft line under the NEW parent is a different row and owes its own firing -- if the latch survived the parent move this reads 1, which is "once per page" rather than once per row');

        Card.Lines.Descr.SetValue('written under H3');
        Assert.AreEqual(2, FiringCount(),
            'writing into a draft line already started must not raise OnNewRecord again');

        Card.Close();
        Assert.AreEqual(0, LineCountFor('H1'), 'H1''s abandoned draft line must not have been written');
        Assert.AreEqual(1, LineCountFor('H3'), 'exactly one line must have been written for H3');
    end;
}
