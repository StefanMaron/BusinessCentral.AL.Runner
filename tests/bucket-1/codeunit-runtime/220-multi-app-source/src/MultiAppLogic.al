// Suite 220: multi-app source compilation
// Verifies that objects from a "second app" compiled together in the same
// source pass are callable by the "first app" test codeunit.
// This exercises the code path fixed in issue #1034 — when .alpackages
// contains the compiled .app of an extension that is also provided as AL
// source, the runner must skip the package reference and compile from source.

codeunit 72001 "Multi App Helper"
{
    procedure GetAnswerToEverything(): Integer
    begin
        exit(42);
    end;

    procedure GetGreeting(Name: Text): Text
    begin
        exit('Hello, ' + Name + '!');
    end;
}

table 72001 "Multi App Entry"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Value; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
