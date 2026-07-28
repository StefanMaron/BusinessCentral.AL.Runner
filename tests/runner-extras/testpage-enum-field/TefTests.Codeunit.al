/// <summary>
/// A TestPage drives an option-typed field by its member NAME — <c>Field.SetValue('High')</c>,
/// <c>Field.Value()</c> returning <c>'High'</c>. That works for both AL spellings of an
/// option: <c>Option</c> with <c>OptionMembers</c> on the field, and <c>Enum</c>, whose members
/// live on a separate object the field only references by id.
///
/// Only the second spelling can go wrong on its own, so every Enum test here is paired with the
/// Option test that would still pass — the pairing is what turns "SetValue is broken" into
/// "enum member metadata never reaches the field".
/// </summary>
codeunit 62131 "TEF Tests"
{
    Subtype = Test;

    local procedure Seed(No: Code[20]) Row: Record "TEF Row"
    var
        Existing: Record "TEF Row";
    begin
        Existing.DeleteAll();
        Row.Init();
        Row."No." := No;
        Row.Insert();
    end;

    [Test]
    procedure SetValue_OnAPlainTextFieldPersistsToTheRow()
    var
        Row: Record "TEF Row";
        Card: TestPage "TEF Card";
    begin
        Seed('T-1');

        Card.OpenEdit();
        Card.First();
        Card.Note.SetValue('edited');
        Card.Close();

        // The outermost control in the suite: no option is involved, so if this fails the defect
        // is that edits to an existing row never reach the table, and every option assertion here
        // is failing for a reason that has nothing to do with option members.
        Row.Get('T-1');
        if Row.Note <> 'edited' then
            Error('Note was <%1>, expected <edited> — the edit never reached the row.', Row.Note);
    end;

    [Test]
    procedure SetValue_ResolvesAnEnumMemberByName()
    var
        Row: Record "TEF Row";
        Card: TestPage "TEF Card";
    begin
        Seed('E-1');

        Card.OpenEdit();
        // OpenEdit does not position the page; the suite is about option values, not about
        // where a freshly opened card lands.
        Card.First();
        Card.Grade.SetValue('High');
        Card.Close();

        Row.Get('E-1');
        // High is 2, so neither the field default nor a failed evaluate landing on 0 can produce
        // this. That is the point of never targeting the first member.
        if Row.Grade <> Row.Grade::High then
            Error('Grade was %1, expected High.', Format(Row.Grade));
    end;

    [Test]
    procedure SetValue_ResolvesAnOptionMemberByName()
    var
        Row: Record "TEF Row";
        Card: TestPage "TEF Card";
    begin
        Seed('O-1');

        Card.OpenEdit();
        // OpenEdit does not position the page; the suite is about option values, not about
        // where a freshly opened card lands.
        Card.First();
        Card.Kind.SetValue('Gamma');
        Card.Close();

        // The control for the test above. If this fails too, the defect is in SetValue itself and
        // has nothing to do with where an option keeps its members.
        Row.Get('O-1');
        if Row.Kind <> Row.Kind::Gamma then
            Error('Kind was %1, expected Gamma.', Format(Row.Kind));
    end;

    [Test]
    procedure Value_ReadsBackAnEnumMemberByName()
    var
        Row: Record "TEF Row";
        Card: TestPage "TEF Card";
    begin
        Row := Seed('E-2');
        Row.Grade := Row.Grade::Mid;
        Row.Modify();

        Card.OpenEdit();
        Card.First();
        // The read direction needs the same member table as the write direction, and a runner can
        // get one working while the other still answers with the ordinal.
        if Card.Grade.Value() <> 'Mid' then
            Error('Grade read back as <%1>, expected <Mid>.', Card.Grade.Value());
        Card.Close();
    end;

    [Test]
    procedure SetValue_RejectsAStringThatIsNotAnEnumMember()
    var
        Row: Record "TEF Row";
        Card: TestPage "TEF Card";
    begin
        Seed('E-3');

        Card.OpenEdit();
        Card.First();
        asserterror Card.Grade.SetValue('Enormous');

        // The load-bearing negative: a runner that "fixes" the evaluate by falling back to 0 for
        // anything it cannot resolve passes all three tests above and fails here. The error must
        // still name the value that could not be resolved.
        if StrPos(GetLastErrorText(), 'Enormous') = 0 then
            Error('Expected the error to name the unresolvable value, but got: %1', GetLastErrorText());

        Row.Get('E-3');
        if Row.Grade <> Row.Grade::Low then
            Error('A rejected SetValue still changed the field to %1.', Format(Row.Grade));
    end;

    [Test]
    procedure SetValue_AcceptsTheOrdinalOfAnEnumMember()
    var
        Row: Record "TEF Row";
        Card: TestPage "TEF Card";
    begin
        Seed('E-4');

        Card.OpenEdit();
        Card.First();
        // AL code in the wild sets option fields both ways; the ordinal path does not need the
        // member table at all, so it should already work and must keep working after the fix.
        Card.Grade.SetValue(2);
        Card.Close();

        Row.Get('E-4');
        if Row.Grade <> Row.Grade::High then
            Error('Grade was %1, expected High.', Format(Row.Grade));
    end;
}
