/// Helper codeunit for CopyArray 3-arg overload (no count parameter).
codeunit 99600 "CA3 Src"
{
    // ── CopyArray 3-arg (no count) ────────────────────────────────────────────

    /// CopyArray(Dest, Src, FromIndex) — copies all elements from FromIndex to end.
    procedure CopyFromIndex(var Src: array[5] of Integer; FromIndex: Integer; var Dest: array[5] of Integer)
    begin
        CopyArray(Dest, Src, FromIndex);
    end;

    /// CopyArray(Dest, Src, 1) — from index 1 means copy everything.
    procedure CopyAll(var Src: array[5] of Integer; var Dest: array[5] of Integer)
    begin
        CopyArray(Dest, Src, 1);
    end;
}
