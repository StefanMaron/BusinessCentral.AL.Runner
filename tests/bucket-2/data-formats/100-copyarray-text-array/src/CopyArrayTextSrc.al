/// Helper codeunit for CopyArray on fixed-length Text arrays.
/// Regression for issue #1232: CS0411 type inference failure when element type
/// is a fixed-length NavText subtype (e.g. Text[1024]).
codeunit 99700 "CA Text Src"
{
    /// CopyArray(Dest, Src, 1) on array[32] of Text[1024].
    procedure CopyTextArray(var Src: array[32] of Text[1024]; var Dest: array[32] of Text[1024])
    begin
        CopyArray(Dest, Src, 1);
    end;

    /// CopyArray(Dest, Src, 2) on array[5] of Text[50] — partial copy from index 2.
    procedure CopyTextArrayFromIndex(var Src: array[5] of Text[50]; FromIndex: Integer; var Dest: array[5] of Text[50])
    begin
        CopyArray(Dest, Src, FromIndex);
    end;
}

/// Page with a global var of array[32] of Text[1024].
/// Regression for issue #1232: CopyArray on page-level array var fails CS0411.
page 99900 "CA Text Page"
{
    PageType = Card;

    var
        MyCaption: array[32] of Text[1024];

    procedure Load(NewCaption: array[32] of Text[1024])
    begin
        CopyArray(MyCaption, NewCaption, 1);
    end;

    procedure GetCaption(Index: Integer): Text[1024]
    begin
        exit(MyCaption[Index]);
    end;
}
