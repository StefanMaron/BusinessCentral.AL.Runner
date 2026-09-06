// Nothing is wrong with this file. It exists so the run has a test object that
// references the malformed table: when the table is dropped from the emit, this
// file is the one that collects an AL0185, which is exactly the misdirection
// issue #2949 is about.
codeunit 60941 "Malformed Key Tests"
{
    Subtype = Test;

    [Test]
    procedure InsertedRowIsCounted()
    var
        Row: Record "Malformed Key Row";
    begin
        Row.Init();
        Row.A := 'x';
        Row.B := 'y';
        Row.Insert();
        if Row.Count() <> 1 then
            Error('expected exactly 1 row, got %1', Row.Count());
    end;
}
