codeunit 305007 "IfcArr Factory Src"
{
    procedure GetValueFromLocalArray(): Integer
    var
        Items: array[3] of Interface "IfcArr Factory Item";
        ImplA: Codeunit "IfcArr Factory Impl A";
        ImplB: Codeunit "IfcArr Factory Impl B";
    begin
        Items[1] := ImplA;
        Items[2] := ImplB;
        Items[3] := ImplA;
        exit(Items[2].GetValue());
    end;

    procedure FillItems(var Items: array[2] of Interface "IfcArr Factory Item")
    var
        ImplA: Codeunit "IfcArr Factory Impl A";
        ImplB: Codeunit "IfcArr Factory Impl B";
    begin
        Items[1] := ImplA;
        Items[2] := ImplB;
    end;
}
