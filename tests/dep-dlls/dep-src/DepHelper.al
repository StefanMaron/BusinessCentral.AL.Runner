// Dependency codeunit — compiled separately via --compile-dep,
// then loaded via --dep-dlls and called from test code.
codeunit 60001 "Dep Helper"
{
    procedure AddNumbers(A: Integer; B: Integer): Integer
    begin
        exit(A + B);
    end;

    procedure GetGreeting(Name: Text): Text
    begin
        exit('Hello, ' + Name + '!');
    end;
}
