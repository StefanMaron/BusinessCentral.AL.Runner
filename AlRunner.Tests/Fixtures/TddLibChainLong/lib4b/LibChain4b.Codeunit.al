/// <summary>A sibling of link 4 of the #5287 chain: "Go" calls the same "M4" on "Lib Chain 3" that lib4's "Lib Chain 4" calls, so the member is already generated, and this compile generates nothing, when this library is compiled after lib4.</summary>
codeunit 72080 "Lib Chain 4b"
{
    procedure Go(): Integer
    var
        Previous: Codeunit "Lib Chain 3";
        Result: Integer;
    begin
        Result := Previous.M4(1);
        exit(Result);
    end;
}
