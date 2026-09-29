// Issues #3029 and #3481. How often a part page's OnNewRecord runs, asserted as DELTAS.
//
// The witness is a COUNT, not an assignment: `Rec.X := 'NEWREC'` is idempotent and passes whether
// the trigger fired once or five times. Every assertion is a delta from a baseline the arm
// measures itself, the same shape corpus codeunit 60358 "ONRC Tests" asserts on real BC. The
// absolute cost of OPENING over an empty part is tier-dependent (3 on the Windows reference
// tier) and the runner does not model a viewport, so it is asserted only as "> 0". Absolutes
// appear only on a part that already has rows, where both tiers measured 0 and +1.
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

    // New() on an empty part: exactly +1 over what opening the card already cost. Opening parks
    // the part on its draft line and starts that row, but New() is not a no-op on it: BC runs its
    // new-record step again (corpus 60358 New_OnEmptyLinkedPart_RunsOnNewRecordOncePlusTheOpenCost,
    // #3029). The write after New() then costs +0.
    [Test]
    procedure New_OnEmptyLinkedPart_RaisesOnNewRecordOnceMoreThanTheOpen()
    var
        Card: TestPage "ONC Card";
        AfterOpen: Integer;
        AfterNew: Integer;
    begin
        Initialize();
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);
        AfterOpen := FiringCount();
        Assert.IsTrue(AfterOpen > 0,
            'opening a card on an empty part must raise the part''s OnNewRecord at least once -- the draft line is shown and started');

        Card.Lines.New();

        AfterNew := FiringCount();
        Assert.AreEqual(AfterOpen + 1, AfterNew,
            'New() on an empty linked part must raise OnNewRecord exactly once more than opening the card already did');

        Card.Lines.Descr.SetValue('typed after New');

        Assert.AreEqual(AfterNew, FiringCount(),
            'writing into the row New() started must not raise OnNewRecord again');

        Card.Close();

        Assert.AreEqual(AfterNew, FiringCount(), 'closing the card must not raise OnNewRecord again');
        Assert.AreEqual(1, LineCountFor('H1'), 'exactly one line must be written for H1');
        Assert.AreEqual(1, LineCountFor('H2'), 'H2''s own line must be untouched');
    end;

    // Merely showing the draft line. Not zero -- corpus 60996 measured that the trigger has
    // already run by this point -- and First() on the already-open empty part adds nothing.
    [Test]
    procedure DraftLine_ShownAndUntouched_FirstAddsNothingToTheOpen()
    var
        Card: TestPage "ONC Card";
        AfterOpen: Integer;
    begin
        Initialize();
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);
        AfterOpen := FiringCount();
        Assert.IsTrue(AfterOpen > 0,
            'opening a card on an empty part must raise the part''s OnNewRecord at least once');

        Assert.IsFalse(Card.Lines.First(),
            'H1 has no lines, so First() must return false and land on the draft line');

        Assert.AreEqual(AfterOpen, FiringCount(),
            'First() on an empty part must not raise OnNewRecord again -- the draft line was started while the card opened');

        Card.Close();

        Assert.AreEqual(AfterOpen, FiringCount(),
            'closing over an untouched draft line must not raise OnNewRecord again');
        Assert.AreEqual(0, LineCountFor('H1'),
            'an untouched draft line must not be written -- so the firings above are not saved rows');
    end;

    // Show the draft line, then write one field: the write costs +0 over the firings before it,
    // because typing commits the row the draft line already started.
    [Test]
    procedure DraftLine_ShownThenWritten_WriteAddsNothing()
    var
        Line: Record "ONC Line";
        Card: TestPage "ONC Card";
        AfterShown: Integer;
    begin
        Initialize();
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);
        Assert.IsFalse(Card.Lines.First(),
            'H1 has no lines, so First() must return false and land on the draft line');
        AfterShown := FiringCount();
        Assert.IsTrue(AfterShown > 0,
            'the draft line must already have raised OnNewRecord before anything is typed');

        Card.Lines.Descr.SetValue('typed into the draft line');

        // Read before Close(), so a firing on the way out cannot be mistaken for one the write
        // caused.
        Assert.AreEqual(AfterShown, FiringCount(),
            'writing into the draft line must not raise OnNewRecord again -- the row was already started when the blank line became current');

        Card.Close();

        Assert.AreEqual(AfterShown, FiringCount(),
            'saving the promoted draft line on close must not raise OnNewRecord again');
        Assert.AreEqual(1, LineCountFor('H1'),
            'typing into the draft line must insert exactly one line for H1');
        Assert.AreEqual(1, LineCountFor('H2'), 'H2''s own line must be untouched');

        Line.SetRange("Header No.", 'H1');
        Line.FindFirst();
        Assert.AreEqual('typed into the draft line', Line.Descr,
            'the typed value must reach the backing table -- the count above is for a row really written');
        Assert.AreEqual('H1', Line."Header No.",
            'the promoted row must still carry the SubPageLink''s value');
    end;

    // The other route onto the draft line: walking off the end of existing data. On a part that
    // HAS rows these are absolutes, and both service tiers measured them (corpus 60358 arm 4).
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
            'AutoSplitKey must still number the promoted line past the line already there');
    end;

    // A part that already HAS rows lands on real data while opening, so opening costs ZERO
    // (both tiers measured 0) and walking across existing rows adds nothing: standing on a row
    // that is already there is not starting a record.
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

    // THE NEGATIVE DIRECTION: two rows cost one firing more than one row. A latch that never
    // reset would pass every +0 above and fail this (corpus 60358
    // TwoRowsWrittenThroughTheDraftLine_SecondRowRaisesOnNewRecordOnceMore).
    [Test]
    procedure TwoRowsThroughTheDraftLine_SecondRowRaisesOnNewRecordOnceMore()
    var
        Card: TestPage "ONC Card";
        AfterShown: Integer;
        AfterFirstRow: Integer;
    begin
        Initialize();
        AddLine('H2', 10000, 'foreign');

        OpenCardOn('H1', Card);
        Assert.IsFalse(Card.Lines.First(), 'H1 has no lines, so First() must return false');
        AfterShown := FiringCount();

        Card.Lines.Descr.SetValue('first line');
        AfterFirstRow := FiringCount();
        Assert.AreEqual(AfterShown, AfterFirstRow,
            'writing the first row must cost nothing beyond the firings the draft line already paid');

        Assert.IsTrue(Card.Lines.Next(),
            'Next() must leave the row just written and land on a fresh draft line');
        Card.Lines.Descr.SetValue('second line');

        Assert.AreEqual(AfterFirstRow + 1, FiringCount(),
            'a second row started through the draft line must raise OnNewRecord exactly once more -- one firing per row, not one per page');

        Card.Close();

        Assert.AreEqual(AfterFirstRow + 1, FiringCount(), 'closing the card must not raise OnNewRecord again');
        Assert.AreEqual(2, LineCountFor('H1'), 'both rows must be written for H1');
        Assert.AreEqual(1, LineCountFor('H2'), 'H2''s own line must be untouched');
    end;

    // The runner's latch is ONCE PER ROW, not once per page: a draft line abandoned by a parent
    // move leaves the next parent's draft line owing its own firing. Every other arm stays
    // within one parent row, so all of them pass with the latch never cleared. Asserted as
    // "more than before the move" rather than an exact figure: what a parent move over an empty
    // part costs is the same tier-dependent render cost as the open, and is not measured.
    [Test]
    procedure DraftLineAbandonedByAParentMove_MakesTheNextRowOweItsOwnFiring()
    var
        Card: TestPage "ONC Card";
        AfterFirstParent: Integer;
        AfterSecondParent: Integer;
    begin
        Initialize();
        AddHeader('H3');

        OpenCardOn('H1', Card);
        Assert.IsFalse(Card.Lines.First(), 'H1 has no lines, so First() must return false');
        AfterFirstParent := FiringCount();
        Assert.IsTrue(AfterFirstParent > 0, 'H1''s draft line must have raised OnNewRecord');

        // Re-point the part at a different parent WITHOUT writing the draft line. The part
        // abandons that line rather than leaving it, which is the path AbandonNewRowLine owns.
        Card.GoToKey('H3');
        Assert.IsFalse(Card.Lines.First(), 'H3 has no lines either, so First() must return false');

        AfterSecondParent := FiringCount();
        Assert.IsTrue(AfterSecondParent > AfterFirstParent,
            'the draft line under the NEW parent is a different row and owes its own firing -- if the latch survived the parent move the count does not move, which is "once per page" rather than once per row');

        Card.Lines.Descr.SetValue('written under H3');
        Assert.AreEqual(AfterSecondParent, FiringCount(),
            'writing into a draft line already started must not raise OnNewRecord again');

        Card.Close();
        Assert.AreEqual(0, LineCountFor('H1'), 'H1''s abandoned draft line must not have been written');
        Assert.AreEqual(1, LineCountFor('H3'), 'exactly one line must have been written for H3');
    end;
}
