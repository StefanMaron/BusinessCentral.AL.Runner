/// <summary>
/// The Field system table (2000000041) is how AL asks "what is this field called?" — the basis
/// for deriving a column name, a label, or a caption-driven default. A runner that serves it for
/// platform tables but not for the ones under test makes that read miss silently: no error, an
/// empty caption, and a blank derived value several steps from the lookup that failed.
/// </summary>
codeunit 62171 "VFC Tests"
{
    Subtype = Test;

    [Test]
    procedure FieldTable_ResolvesTheCaptionOfARunnerCompiledField()
    var
        TargetField: Record Field;
    begin
        if not TargetField.Get(Database::"VFC Row", 20) then
            Error('Field.Get(VFC Row, 20) found no row in the Field virtual table.');

        // The exact caption, not just non-empty: AL derives an identifier from this text.
        if TargetField."Field Caption" <> 'New Column Name' then
            Error('Field Caption was <%1>, expected <New Column Name>.', TargetField."Field Caption");
    end;

    [Test]
    procedure FieldTable_ReportsAMissingFieldAsNotFound()
    var
        TargetField: Record Field;
    begin
        // The negative: a field number that does not exist must be reported as absent rather than
        // answered with a blank row, which is what makes the positive above meaningful.
        if TargetField.Get(Database::"VFC Row", 999) then
            Error('Field.Get(VFC Row, 999) returned a row for a field that does not exist.');
    end;

    [Test]
    procedure FieldCaption_ReturnsTheDeclaredCaption()
    var
        Row: Record "VFC Row";
    begin
        // Rec.FieldCaption is the other way AL asks the same question, and it is what error
        // messages are built from ("You must specify %1"). Answering '' makes those messages
        // name nothing at all, and makes any test asserting on them pass while asserting nothing.
        if Row.FieldCaption(NewColumnName) <> 'New Column Name' then
            Error('FieldCaption(20) was <%1>, expected <New Column Name>.',
                Row.FieldCaption(NewColumnName));
    end;

    [Test]
    procedure FieldCaption_FallsBackToTheNameWhenNoCaptionIsDeclared()
    var
        Row: Record "VFC Row";
    begin
        // The control: a field with no Caption property reports its name, so a green result
        // above cannot be produced by simply echoing the name for everything.
        if Row.FieldCaption("No.") <> 'No.' then
            Error('FieldCaption(1) was <%1>, expected <No.>.', Row.FieldCaption("No."));
    end;
}
