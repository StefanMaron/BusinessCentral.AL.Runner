/// <summary>
/// TestPage must report a control's real Editable/Enabled state.
///
/// A page protects data it does not own with control properties: <c>Editable = false</c> for
/// a control that is never writable, <c>Editable = SomeVar</c> for one that depends on the
/// row. Those properties ARE the read-only contract, and a TestPage is the only thing that
/// can test them.
///
/// A runner that answers <c>Editable()</c> with a constant true makes every such test
/// unfailable. That is worse than a missing feature: the tests exist, they are green, and
/// they assert nothing — so a regression that makes protected rows writable ships unnoticed.
///
/// The default-editable and value-still-readable cases are load-bearing negatives: they are
/// what a "just return false" fix fails.
/// </summary>
codeunit 62091 "TPE Tests"
{
    Subtype = Test;

    local procedure SeedRows()
    var
        Row: Record "TPE Row";
    begin
        Row.DeleteAll();

        Row.Init();
        Row."No." := 'OPEN';
        Row.Name := 'open row';
        Row.Note := 'note-open';
        Row.Locked := false;
        Row.Insert();

        Row.Init();
        Row."No." := 'LOCKED';
        Row.Name := 'locked row';
        Row.Note := 'note-locked';
        Row.Locked := true;
        Row.Insert();
    end;

    local procedure OpenCardOn(No: Code[20]) Card: TestPage "TPE Card"
    var
        Row: Record "TPE Row";
    begin
        Row.Get(No);
        Card.OpenEdit();
        Card.GoToRecord(Row);
    end;

    [Test]
    procedure ConstantEditableFalse_IsReportedAsNotEditable()
    var
        Card: TestPage "TPE Card";
    begin
        SeedRows();
        Card := OpenCardOn('OPEN');

        // Editable = false is a compile-time constant on the control. Even on a row the page
        // considers fully editable, this control must not be.
        if Card."No.".Editable() then
            Error('"No.".Editable() was true, but the control declares Editable = false.');

        Card.Close();
    end;

    [Test]
    procedure NoEditableProperty_StaysEditable()
    var
        Card: TestPage "TPE Card";
    begin
        SeedRows();
        Card := OpenCardOn('OPEN');

        // The default. A fix that reports false for everything passes every other test here
        // and fails this one.
        if not Card.Note.Editable() then
            Error('Note.Editable() was false, but the control declares no Editable property.');

        Card.Close();
    end;

    [Test]
    procedure EditableBoundToPageVariable_FollowsTheRow()
    var
        Card: TestPage "TPE Card";
    begin
        SeedRows();

        // Editable = RowEditable, and OnAfterGetRecord sets RowEditable := not Rec.Locked.
        Card := OpenCardOn('OPEN');
        if not Card.Name.Editable() then
            Error('Name.Editable() was false on the unlocked row, expected true.');
        Card.Close();

        Clear(Card);
        Card := OpenCardOn('LOCKED');
        if Card.Name.Editable() then
            Error('Name.Editable() was true on the locked row, expected false.');
        Card.Close();
    end;

    [Test]
    procedure NotEditableControl_StillReadsItsValue()
    var
        Card: TestPage "TPE Card";
    begin
        SeedRows();
        Card := OpenCardOn('LOCKED');

        // Not editable is not the same as not readable. A card shows read-only data; a fix
        // that suppressed the value along with the editability would break every list test.
        if Card.Name.Value() <> 'locked row' then
            Error('Name.Value() on the read-only row was <%1>, expected <locked row>.',
                Card.Name.Value());

        Card.Close();
    end;

    [Test]
    procedure ActionEnabledBoundToPageVariable_FollowsTheRow()
    var
        Card: TestPage "TPE Card";
    begin
        SeedRows();

        Card := OpenCardOn('OPEN');
        if not Card.Rename.Enabled() then
            Error('Rename.Enabled() was false on the unlocked row, expected true.');
        Card.Close();

        Clear(Card);
        Card := OpenCardOn('LOCKED');
        if Card.Rename.Enabled() then
            Error('Rename.Enabled() was true on the locked row, expected false.');
        // An action with no Enabled property is the negative: it must stay enabled.
        if not Card.Refresh.Enabled() then
            Error('Refresh.Enabled() was false, but the action declares no Enabled property.');
        Card.Close();
    end;

    [Test]
    procedure CurrPageEditable_IsReflectedByTheTestPage()
    var
        Card: TestPage "TPE Card";
    begin
        SeedRows();

        // Page-level editability is a separate mechanism from the per-control property, set
        // here by OnAfterGetRecord calling CurrPage.Editable(not Rec.Locked).
        Card := OpenCardOn('OPEN');
        if not Card.Editable() then
            Error('TestPage.Editable() was false on the unlocked row, expected true.');
        Card.Close();

        Clear(Card);
        Card := OpenCardOn('LOCKED');
        if Card.Editable() then
            Error('TestPage.Editable() was true on the locked row, expected false.');
        Card.Close();
    end;
}
