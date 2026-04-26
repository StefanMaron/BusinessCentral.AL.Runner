codeunit 1282001 "Inv Ext Caller"
{
    /// <summary>
    /// Calls a method defined in a page extension via a Page variable.
    /// BC emits page.Invoke(extensionId, memberId, args) — the 3-arg overload.
    /// </summary>
    procedure CallExtMethod(Input: Integer): Integer
    var
        P: Page "Inv Ext Pg";
    begin
        exit(P.GetExtNumber(Input));
    end;

    procedure CallBaseMethod(): Integer
    var
        P: Page "Inv Ext Pg";
    begin
        exit(P.GetBaseNumber());
    end;
}
