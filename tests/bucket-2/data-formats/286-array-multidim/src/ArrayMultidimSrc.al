/// Source codeunit exercising multi-dimensional arrays and GetSubArray — issue #974.
/// BC emits arr[r, c] (2-arg indexer) for 2D arrays and arr.GetSubArray(rowIndex)
/// when passing a 2D array row to a 1D array parameter.
codeunit 134001 "AMD Source"
{
    /// Build a 2×3 integer array and return element [r, c] (1-based).
    procedure Get2DElement(r: Integer; c: Integer): Integer
    var
        arr: Array[2, 3] of Integer;
    begin
        arr[1, 1] := 11; arr[1, 2] := 12; arr[1, 3] := 13;
        arr[2, 1] := 21; arr[2, 2] := 22; arr[2, 3] := 23;
        exit(arr[r, c]);
    end;

    /// Set a value in a 2D array and return it via 2-arg indexer.
    procedure Set2DElement(): Integer
    var
        arr: Array[2, 3] of Integer;
    begin
        arr[2, 3] := 99;
        exit(arr[2, 3]);
    end;

    /// Helper accepting a 1D row.
    procedure SumRow(row: Array[3] of Integer): Integer
    begin
        exit(row[1] + row[2] + row[3]);
    end;

    /// Pass a row of a 2D array to a 1D array parameter.
    /// BC emits: _parent.SumRow(this.arr.GetSubArray(0)) for arr[1].
    procedure SumFirstRow(): Integer
    var
        arr: Array[2, 3] of Integer;
    begin
        arr[1, 1] := 10; arr[1, 2] := 20; arr[1, 3] := 30;
        arr[2, 1] := 1; arr[2, 2] := 2; arr[2, 3] := 3;
        exit(SumRow(arr[1]));
    end;

    /// Verify GetSubArray returns the correct second row.
    procedure SumSecondRow(): Integer
    var
        arr: Array[2, 3] of Integer;
    begin
        arr[1, 1] := 1; arr[1, 2] := 2; arr[1, 3] := 3;
        arr[2, 1] := 40; arr[2, 2] := 50; arr[2, 3] := 60;
        exit(SumRow(arr[2]));
    end;
}
