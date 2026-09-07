/// Issue #3055 -- a TestPage SetValue that writes back the value the field ALREADY held used to
/// run the table's OnModify, because LiveNavTestPage.FlushPendingModify wrote unconditionally
/// from a flag MarkEdited set on any assignment. Real BC gates the page write on a value
/// comparison instead (NavForm.SaveRecordAsync: CompareAllNormalFields against OldRecord, ORed
/// with RecordImplementation.HasChangedFields, which reaches
/// MutableRecordBuffer.HasActualChangedValues -- itself a per-field
/// IsChangedValueSameAsOriginalValue check). So real BC issues no Modify and runs no OnModify.
///
/// WHAT THIS SUITE IS AND IS NOT. The BC-behaviour claim -- what a real service tier does for
/// this AL shape -- is measured upstream on eight BC versions in
/// StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60411 "SVM Tests" (corpus PR #271),
/// which is where it belongs. This suite pins the runner-internal consequence so a regression is
/// caught by this repository's own CI on every leg, and so the coalescing property below --
/// which the corpus test also covers but which is a statement about the runner's flush model --
/// has a home here.
///
/// Every assertion is a concrete integer. None of them passes against an implementation that
/// never fires OnModify, and none passes against one that fires it per assignment.
codeunit 65764 "Tsvm Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tsvm Assert";

    local procedure SeedRow(No: Code[20]; NoteValue: Text[50])
    var
        Row: Record "Tsvm Row";
        Trace: Record "Tsvm Trace";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."No." := No;
        Row.Note := NoteValue;
        Row.Insert();

        // Reset AFTER the seed insert, so nothing the setup did is counted as an edit.
        Trace.Reset();
    end;

    [Test]
    procedure DirectModify_RunsOnModify_SoTheTallyObservesIt()
    var
        Row: Record "Tsvm Row";
        Trace: Record "Tsvm Trace";
    begin
        // FIXTURE CONTROL, and it is not ceremony: every other test here reads a count of 0 or
        // 1, and a tally that could never observe OnModify at all would read 0 for the headline
        // case and look like a pass. This proves the instrument works before anything uses it.
        SeedRow('TSVM-0', 'original');

        Row.Get('TSVM-0');
        Row.Note := 'moved';
        Row.Modify(true);

        Assert.AreEqual(1, Trace.Count(), 'A direct Modify(true) must run OnModify exactly once.');
    end;

    [Test]
    procedure OpenAndCloseWithoutWriting_RunsNoOnModify()
    var
        Trace: Record "Tsvm Trace";
        Card: TestPage "Tsvm Card";
    begin
        // The other direction of the same control: merely opening a card on a row and closing it
        // must not write. Without this, a 0 in the same-value test below could be produced by a
        // page that never saves anything at all.
        SeedRow('TSVM-1', 'original');

        Card.OpenEdit();
        Card.GoToKey('TSVM-1');
        Card.Close();

        Assert.AreEqual(0, Trace.Count(),
            'Opening and closing a card without assigning any control must not run OnModify.');
    end;

    [Test]
    procedure SetValue_WithADifferentValue_RunsOnModifyExactlyOnce()
    var
        Row: Record "Tsvm Row";
        Trace: Record "Tsvm Trace";
        Card: TestPage "Tsvm Card";
    begin
        // The positive case the gate must NOT break. A gate that simply stopped writing would
        // pass the same-value test and fail this one.
        SeedRow('TSVM-2', 'original');

        Card.OpenEdit();
        Card.GoToKey('TSVM-2');
        Card.Note.SetValue('changed');
        Card.Close();

        Assert.AreEqual(1, Trace.Count(),
            'A SetValue writing a DIFFERENT value must run OnModify exactly once.');

        Row.Get('TSVM-2');
        Assert.AreEqualText('changed', Row.Note, 'The differing value must have been persisted.');
    end;

    [Test]
    procedure SetValue_WithTheSameValue_RunsNoOnModify()
    var
        Row: Record "Tsvm Row";
        Trace: Record "Tsvm Trace";
        Card: TestPage "Tsvm Card";
    begin
        // THE HEADLINE. Before #3055 this read 1: MarkEdited set _pendingModify on the
        // assignment and FlushPendingModify wrote from that flag alone.
        SeedRow('TSVM-3', 'original');

        Card.OpenEdit();
        Card.GoToKey('TSVM-3');
        Card.Note.SetValue('original');
        Card.Close();

        Assert.AreEqual(0, Trace.Count(),
            'A SetValue writing back the value the field already held must NOT run OnModify.');

        Row.Get('TSVM-3');
        Assert.AreEqualText('original', Row.Note,
            'The value must be unchanged whether or not a Modify was issued.');
    end;

    [Test]
    procedure SetValue_SameThenDifferent_RunsOnModifyOnce()
    var
        Row: Record "Tsvm Row";
        Trace: Record "Tsvm Trace";
        Card: TestPage "Tsvm Card";
    begin
        // Two assignments, one row, one open page. Separates "the gate is evaluated per
        // assignment" (which would give 2) from "the gate is evaluated once per row at flush,
        // against the values the row ended up with" (which gives 1), and rules out a same-value
        // write POISONING a later genuine one by clearing the pending flag.
        SeedRow('TSVM-4', 'original');

        Card.OpenEdit();
        Card.GoToKey('TSVM-4');
        Card.Note.SetValue('original');
        Card.Note.SetValue('final');
        Card.Close();

        Assert.AreEqual(1, Trace.Count(),
            'A same-value write followed by a differing one must run OnModify exactly once.');

        Row.Get('TSVM-4');
        Assert.AreEqualText('final', Row.Note, 'The last written value must be the persisted one.');
    end;

    [Test]
    procedure SetValue_ThereAndBackAgain_RunsNoOnModify()
    var
        Row: Record "Tsvm Row";
        Trace: Record "Tsvm Trace";
        Card: TestPage "Tsvm Card";
    begin
        // The case that decides WHICH comparison the gate uses. Writing 'moved' and then
        // 'original' leaves the row's values identical to the ones it was loaded with, even
        // though a genuinely different value passed through the control on the way. A gate
        // remembering "some assignment differed at the time it happened" would write; BC's,
        // which compares the record against OldRecord at save time, does not.
        SeedRow('TSVM-5', 'original');

        Card.OpenEdit();
        Card.GoToKey('TSVM-5');
        Card.Note.SetValue('moved');
        Card.Note.SetValue('original');
        Card.Close();

        Assert.AreEqual(0, Trace.Count(),
            'Writing a different value and then writing the original back must NOT run OnModify.');

        Row.Get('TSVM-5');
        Assert.AreEqualText('original', Row.Note, 'The row must hold the value it started with.');
    end;
}
