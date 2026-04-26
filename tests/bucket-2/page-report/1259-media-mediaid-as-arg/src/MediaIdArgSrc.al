table 1259001 "Media Id Arg Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Image; Media) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

codeunit 1259001 "Media Id Arg Src"
{
    /// Accepts a Guid argument and returns it — used to prove
    /// that MediaId() can be passed as an argument.
    procedure GetGuidBack(Id: Guid): Guid
    begin
        exit(Id);
    end;
}
