namespace My.App;

using Totally.Nonexistent.Namespace; // no such namespace, nothing referenced
using Another.Made.Up.Ns;
using Third.Ghost.Namespace;

codeunit 50450 "Namespace Thing"
{
    procedure Compute(A: Integer; B: Integer): Integer
    begin
        exit(A + B);
    end;
}
