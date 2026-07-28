/// <summary>
/// Record.Get must raise when the row does not exist and the caller does not consume the
/// return value.
///
/// AL picks the failure mode from the call site: `if Rec.Get(x) then` traps the error and
/// yields false, while a bare `Rec.Get(x);` statement raises. The al-language corpus only
/// covers the trapping form. A runner that silently succeeds on the raising form leaves the
/// caller holding a blank or stale record, and every assertion after it is then testing
/// something that never happened.
/// </summary>
codeunit 62061 "RGN Tests"
{
    Subtype = Test;

    local procedure SeedRow(No: Code[20]; Description: Text[50])
    var
        Row: Record "RGN Row";
    begin
        Row.Init();
        Row."No." := No;
        Row.Description := Description;
        Row.Insert();
    end;

    [Test]
    procedure GetMissingRow_AsStatement_Raises()
    var
        Row: Record "RGN Row";
    begin
        Row.DeleteAll();
        SeedRow('EXISTS-1', 'seeded');

        // The return value is deliberately not consumed, so this must raise rather than
        // leave Row holding whatever it held before.
        asserterror Row.Get('MISSING-1');
        if StrPos(GetLastErrorText(), 'does not exist') = 0 then
            Error('Expected BC''s record-not-found error, got: <%1>', GetLastErrorText());
    end;

    [Test]
    procedure GetMissingRow_DoesNotLeaveStaleDataBehind()
    var
        Row: Record "RGN Row";
    begin
        Row.DeleteAll();
        SeedRow('EXISTS-1', 'seeded');

        Row.Get('EXISTS-1');
        if Row.Description <> 'seeded' then
            Error('Precondition failed: reading the existing row gave <%1>.', Row.Description);

        // The failed Get must not silently succeed and hand back the PREVIOUS row's contents.
        // This is the concrete damage a silently-succeeding Get does: the caller reads
        // 'seeded' for a key that was never there.
        asserterror Row.Get('MISSING-1');
        if StrPos(GetLastErrorText(), 'does not exist') = 0 then
            Error('Expected the failed Get to raise, got: <%1>', GetLastErrorText());
    end;

    [Test]
    procedure GetMissingRow_WhenReturnValueConsumed_ReturnsFalseAndDoesNotRaise()
    var
        Row: Record "RGN Row";
        Found: Boolean;
    begin
        Row.DeleteAll();
        SeedRow('EXISTS-1', 'seeded');

        // The other direction, and the one the corpus already covers: consuming the result
        // traps the error. A fix that made every Get raise would break this.
        Found := Row.Get('MISSING-1');
        if Found then
            Error('Get on a missing row returned true.');

        Found := Row.Get('EXISTS-1');
        if not Found then
            Error('Get on an existing row returned false.');
        if Row.Description <> 'seeded' then
            Error('Get on an existing row loaded <%1>, expected <seeded>.', Row.Description);
    end;
}
