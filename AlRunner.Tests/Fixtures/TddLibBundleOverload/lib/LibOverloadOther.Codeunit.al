/// <summary>
/// The library's second object: it touches nothing missing, so a compile against the app's old
/// symbols drops "Lib Overload Helper" and keeps this one (a partial result, not an empty one).
/// </summary>
codeunit 65561 "Lib Overload Other"
{
    procedure Seven(): Integer
    begin
        exit(7);
    end;
}
