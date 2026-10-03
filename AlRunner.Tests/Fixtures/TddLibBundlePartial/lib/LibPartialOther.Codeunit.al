/// <summary>
/// The library's second object: it touches nothing missing, so a compile against the app's old
/// symbols drops "Lib Partial Helper" and keeps this one (a partial result, not an empty one).
/// </summary>
codeunit 65531 "Lib Partial Other"
{
    procedure Seven(): Integer
    begin
        exit(7);
    end;
}
