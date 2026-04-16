/// Helper codeunit exercising Database.SID().
codeunit 81400 "DS Src"
{
    /// Returns the result of Database.SID().
    procedure GetSid(): Text
    begin
        exit(Database.SID());
    end;

    /// Returns true if SID is non-empty.
    procedure SidIsNonEmpty(): Boolean
    begin
        exit(Database.SID() <> '');
    end;

    /// Returns the length of the SID string.
    procedure SidLength(): Integer
    begin
        exit(StrLen(Database.SID()));
    end;
}
