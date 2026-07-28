/// <summary>
/// <c>Field.SetValue(x)</c> on a page is a VALIDATE, not an assignment — that is what makes a
/// card fill in a caption when you pick an id, and what makes it refuse a value that breaks an
/// invariant.
///
/// The failure mode a raw assignment produces is quietly misleading: the field the test wrote is
/// exactly right when read back, and only the fields DERIVED from it are wrong. The test then
/// fails naming the derived field, which is the one place the defect is not.
/// </summary>
codeunit 62141 "TOV Tests"
{
    Subtype = Test;

    local procedure Seed(No: Code[20])
    var
        Row: Record "TOV Row";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."No." := No;
        Row.Insert();
    end;

    [Test]
    procedure SetValue_RunsTheTableFieldsOnValidate()
    var
        Row: Record "TOV Row";
        Card: TestPage "TOV Card";
    begin
        Seed('V-1');

        Card.OpenEdit();
        Card.First();
        Card.Source.SetValue('ABC');
        Card.Close();

        Row.Get('V-1');
        // Derived is written by nothing except Source's OnValidate.
        if Row.Derived <> 'derived-from-ABC' then
            Error('Derived was <%1>, expected <derived-from-ABC> — OnValidate did not run.', Row.Derived);
        // ...and the value the test actually wrote still landed. A "fix" that ran the trigger but
        // dropped the assignment would fail here.
        if Row.Source <> 'ABC' then
            Error('Source was <%1>, expected <ABC>.', Row.Source);
    end;

    [Test]
    procedure SetValue_RunsTheControlsOwnOnValidate()
    var
        Row: Record "TOV Row";
        Card: TestPage "TOV Card";
    begin
        Seed('V-2');

        Card.OpenEdit();
        Card.First();
        Card.Watched.SetValue('XY');
        Card.Close();

        Row.Get('V-2');
        // The page control's trigger, not the table's. The Manual field it is bound to has no
        // table-level OnValidate, so PageEcho can only have been written by the control.
        if Row.PageEcho <> 'control-saw-XY' then
            Error('PageEcho was <%1>, expected <control-saw-XY> — the control''s OnValidate did not run.',
                Row.PageEcho);
    end;

    [Test]
    procedure SetValue_OnAFieldWithNoTriggerJustWritesIt()
    var
        Row: Record "TOV Row";
        Card: TestPage "TOV Card";
    begin
        Seed('V-3');

        Card.OpenEdit();
        Card.First();
        Card.Manual.SetValue('plain');
        Card.Close();

        // The control: most fields have no OnValidate, and they must still be written. This is
        // what a fix that only ever validated-and-never-assigned would break.
        Row.Get('V-3');
        if Row.Manual <> 'plain' then
            Error('Manual was <%1>, expected <plain>.', Row.Manual);
    end;

    [Test]
    procedure SetValue_PropagatesAnErrorRaisedByOnValidate()
    var
        Row: Record "TOV Row";
        Card: TestPage "TOV Card";
    begin
        Seed('V-4');

        Card.OpenEdit();
        Card.First();
        asserterror Card.Guarded.SetValue(-5);

        // The load-bearing negative. A validate whose error is swallowed is worse than no
        // validate at all: the page reports success and the rejected value is sitting in the row.
        if StrPos(GetLastErrorText(), 'may not be negative') = 0 then
            Error('Expected the OnValidate error to surface, but got: %1', GetLastErrorText());

        Card.Close();

        Row.Get('V-4');
        if Row.Guarded <> 0 then
            Error('A value OnValidate rejected was stored anyway: %1.', Row.Guarded);
    end;
}
