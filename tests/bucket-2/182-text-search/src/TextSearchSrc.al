/// Helper codeunit exercising Text search/navigation methods.
codeunit 60070 "TXS Src"
{
    procedure Contains_Positive(haystack: Text; needle: Text): Boolean
    begin
        exit(haystack.Contains(needle));
    end;

    procedure StartsWith_It(haystack: Text; prefix: Text): Boolean
    begin
        exit(haystack.StartsWith(prefix));
    end;

    procedure EndsWith_It(haystack: Text; suffix: Text): Boolean
    begin
        exit(haystack.EndsWith(suffix));
    end;

    procedure IndexOfIt(haystack: Text; needle: Text): Integer
    begin
        // AL convention: 1-based, 0 when not found.
        exit(haystack.IndexOf(needle));
    end;

    procedure LastIndexOfIt(haystack: Text; needle: Text): Integer
    begin
        exit(haystack.LastIndexOf(needle));
    end;
}
