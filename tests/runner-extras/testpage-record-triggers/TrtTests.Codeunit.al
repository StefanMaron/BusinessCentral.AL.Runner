/// <summary>
/// The standard AL new-record flow is <c>OpenNew()</c>, set a few fields, <c>OK().Invoke()</c>,
/// and everything that makes the resulting row CORRECT lives in the page's record triggers:
/// OnNewRecord seeds the defaults a blank record does not have, OnInsertRecord gets the last
/// word before the persist.
///
/// A runner that skips them still inserts a row — just not the row the page would ever have
/// produced. The test then fails complaining about a field value, several layers from the
/// trigger that never ran, which reads like an application bug rather than a missing trigger.
/// </summary>
codeunit 62112 "TRT Tests"
{
    Subtype = Test;

    local procedure Reset()
    var
        Row: Record "TRT Row";
        Echo: Record "TRT Echo";
    begin
        Row.DeleteAll();
        Echo.DeleteAll();
    end;

    [Test]
    procedure OpenNew_RunsOnNewRecordBeforeTheFieldsAreSet()
    var
        Row: Record "TRT Row";
        Card: TestPage "TRT Card";
    begin
        Reset();

        Card.OpenNew();
        Card."No.".SetValue('NEW-1');
        Card.OK().Invoke();

        Row.Get('NEW-1');
        // Kind::Tenant is 1 and only OnNewRecord sets it; a blank record carries Extension (0).
        if Row.Kind <> Row.Kind::Tenant then
            Error('Kind was %1, expected Tenant — OnNewRecord did not run.', Format(Row.Kind));
    end;

    [Test]
    procedure OnInsertRecord_RunsBeforeTheRowIsPersisted()
    var
        Row: Record "TRT Row";
        Card: TestPage "TRT Card";
    begin
        Reset();

        Card.OpenNew();
        Card."No.".SetValue('NEW-2');
        Card.Note.SetValue('typed-by-user');
        Card.OK().Invoke();

        Row.Get('NEW-2');
        // The trigger overwrites what the user typed, so a stale value proves it never ran
        // rather than merely proving something was written.
        if Row.Note <> 'stamped-by-oninsert' then
            Error('Note was <%1>, expected <stamped-by-oninsert> — OnInsertRecord did not run.',
                Row.Note);
    end;

    [Test]
    procedure OnInsertRecord_ReturningFalse_SuppressesTheInsert()
    var
        Row: Record "TRT Row";
        Card: TestPage "TRT Card No Insert";
    begin
        Reset();

        Card.OpenNew();
        Card."No.".SetValue('VETOED');
        Card.OK().Invoke();

        // The negative that gives the trigger meaning: its RETURN VALUE decides whether the
        // row is written at all. A runner that runs the trigger and ignores the result passes
        // every other test here and fails this one.
        if Row.Get('VETOED') then
            Error('The row was inserted even though OnInsertRecord returned false.');
    end;

    [Test]
    procedure OK_PersistsTheNewRowImmediately()
    var
        Row: Record "TRT Row";
        Card: TestPage "TRT Card";
    begin
        Reset();

        Card.OpenNew();
        Card."No.".SetValue('NEW-3');
        Card.OK().Invoke();

        // Right after OK, before Close or Dispose. Persisting only at teardown means every
        // assertion a test makes between the two reads a table that does not have the row yet.
        if not Row.Get('NEW-3') then
            Error('The row was not persisted by OK().Invoke().');
    end;

    [Test]
    procedure Cancel_DiscardsTheNewRow()
    var
        Row: Record "TRT Row";
        Card: TestPage "TRT Card";
    begin
        Reset();

        Card.OpenNew();
        Card."No.".SetValue('ABANDONED');
        Card.Cancel().Invoke();

        // The other direction: closing without OK must not write. A fix that made OK persist
        // by simply always flushing would fail here.
        if Row.Get('ABANDONED') then
            Error('A cancelled new row was persisted anyway.');
    end;

    [Test]
    procedure OnOpenPage_RunsBeforeThePageIsRead()
    var
        Row: Record "TRT Row";
        Card: TestPage "TRT Singleton Card";
    begin
        Reset();

        Card.OpenEdit();

        // The page had no row to open on; OnOpenPage is what creates and selects one.
        if Card."No.".Value() <> 'SINGLETON' then
            Error('The page opened on <%1>, expected SINGLETON — OnOpenPage did not run.',
                Card."No.".Value());
        if not Row.Get('SINGLETON') then
            Error('OnOpenPage did not create the singleton row.');

        Card.Close();
    end;

    [Test]
    procedure OnOpenPage_LeavesTheRecordUsableByThePagesOwnActions()
    var
        Row: Record "TRT Row";
        Card: TestPage "TRT Singleton Card";
    begin
        Reset();

        Card.OpenEdit();
        // The real consequence: an action that Modifies the row OnOpenPage fetched. On an
        // unpositioned record this fails with "the row does not exist" naming a blank key,
        // which is how the missing trigger actually surfaced.
        Card.Stamp.Invoke();
        Card.Close();

        Row.Get('SINGLETON');
        if Row.Note <> 'stamped' then
            Error('The action''s Modify did not reach the row: Note is <%1>.', Row.Note);
    end;

    [Test]
    procedure OnClosePage_RunsWhenThePageIsClosed()
    var
        Echo: Record "TRT Echo";
        Card: TestPage "TRT Singleton Card";
    begin
        Reset();

        Card.OpenEdit();
        if Echo.Get('CLOSED') then
            Error('OnClosePage ran before the page was closed.');

        Card.Close();

        if not Echo.Get('CLOSED') then
            Error('OnClosePage did not run on Close().');
    end;

    [Test]
    procedure OnAfterGetCurrRecord_RunsWhenTheCursorMoves()
    var
        Row: Record "TRT Row";
        Echo: Record "TRT Echo";
        Card: TestPage "TRT Card";
        Before: Integer;
    begin
        Reset();

        Row.Init();
        Row."No." := 'A';
        Row.Insert();
        Row.Init();
        Row."No." := 'B';
        Row.Insert();

        Card.OpenEdit();
        Card.First();
        if Echo.Get('CURR') then
            Before := Echo.Hits;

        Card.Next();

        // OnAfterGetCurrRecord is the trigger that fires on EVERY navigation, including one to
        // an already-fetched record — which is why a page that must refresh derived state on
        // every move uses it rather than OnAfterGetRecord.
        if not Echo.Get('CURR') then
            Error('OnAfterGetCurrRecord never ran.');
        if Echo.Hits <= Before then
            Error('OnAfterGetCurrRecord did not run on Next(): hits stayed at %1.', Echo.Hits);
    end;
}
