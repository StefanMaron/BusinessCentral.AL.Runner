/// <summary>
/// A test library codeunit (not a test codeunit) that calls CountPending, a procedure
/// "Tdd Helper Target Cu" does not declare yet. "Tdd Helper Tests" reaches it from a [Test] (#5147).
/// </summary>
codeunit 65052 "Tdd Helper Library"
{
    procedure CountPending(): Integer
    var
        Target: Codeunit "Tdd Helper Target Cu";
        Result: Integer;
    begin
        Result := Target.CountPending(4);
        exit(Result);
    end;
}
