interface "IC Source List"
{
    procedure GetCount(): Integer;
    procedure GetName(): Text;
}

codeunit 1900004 "IC Source List Impl A" implements "IC Source List"
{
    procedure GetCount(): Integer
    begin
        exit(42);
    end;

    procedure GetName(): Text
    begin
        exit('ImplA');
    end;
}

codeunit 1900005 "IC Source List Impl B" implements "IC Source List"
{
    procedure GetCount(): Integer
    begin
        exit(99);
    end;

    procedure GetName(): Text
    begin
        exit('ImplB');
    end;
}
