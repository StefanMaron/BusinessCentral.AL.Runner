// App B defines a codeunit with the SAME name "Shared Helper" — this is a
// genuine collision (Codeunit is not an extension type) and must NOT be suppressed.
codeunit 56311 "Shared Helper"
{
    procedure GetValue(): Text
    begin
        exit('FromB');
    end;
}
