codeunit 70005 "Probe Helper"
{
    var
        M: Codeunit "Missing Collab"; // declared by no app: --tdd generates it; only Unused() mentions it

    procedure Answer(): Integer
    begin
        exit(42);
    end;

    procedure Unused()
    begin
        M.Run();
    end;
}
