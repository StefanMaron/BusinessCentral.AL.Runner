/// Helper codeunit exercising ErrorInfo.Callstack().
codeunit 59880 "EIC Src"
{
    procedure GetCallstack(ei: ErrorInfo): Text
    begin
        exit(ei.Callstack());
    end;

    procedure GetCallstackFromFreshErrorInfo(): Text
    var
        ei: ErrorInfo;
    begin
        // A default-initialised ErrorInfo has no real call stack context —
        // Callstack() should return a non-null Text (empty is acceptable).
        exit(ei.Callstack());
    end;

    procedure CallstackIsText(ei: ErrorInfo): Boolean
    var
        cs: Text;
    begin
        // Proving: the return value is a Text (assignment must compile and complete).
        cs := ei.Callstack();
        exit(true);
    end;
}
