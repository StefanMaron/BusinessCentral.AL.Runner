// App A defines a codeunit named "Shared Helper" — NOT an extension type,
// so a same-name collision with App B must NOT be suppressed.
codeunit 56310 "Shared Helper"
{
    procedure GetValue(): Text
    begin
        exit('FromA');
    end;
}
