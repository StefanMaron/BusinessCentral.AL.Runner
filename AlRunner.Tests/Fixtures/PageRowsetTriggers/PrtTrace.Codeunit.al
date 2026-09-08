// Records the exact Which/Steps values the runner hands the triggers, in order. The corpus
// asserts the ROWS a walk produces, because how many times a real client calls a trigger is a
// client-side detail it may not pin; this fixture asserts the runner's own call sequence,
// which is the thing a regression here would change first.
codeunit 70642 "PRT Trace"
{
    SingleInstance = true;

    var
        Entries: Text;

    procedure Reset()
    begin
        Entries := '';
    end;

    procedure Note(Entry: Text)
    begin
        Entries += Entry;
    end;

    procedure Get(): Text
    begin
        exit(Entries);
    end;
}
