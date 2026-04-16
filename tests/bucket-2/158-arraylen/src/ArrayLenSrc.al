/// Helper codeunit exercising ArrayLen() for 1-D and 2-D arrays.
codeunit 61800 "AL Helper"
{
    procedure GetLen1D(): Integer
    var
        Arr: array[10] of Integer;
    begin
        exit(ArrayLen(Arr));
    end;

    procedure GetLen1D_Dim1(): Integer
    var
        Arr: array[10] of Integer;
    begin
        exit(ArrayLen(Arr, 1));
    end;

    procedure GetLen2D_Dim1(): Integer
    var
        Arr: array[3, 4] of Integer;
    begin
        exit(ArrayLen(Arr, 1));
    end;

    procedure GetLen2D_Dim2(): Integer
    var
        Arr: array[3, 4] of Integer;
    begin
        exit(ArrayLen(Arr, 2));
    end;
}
