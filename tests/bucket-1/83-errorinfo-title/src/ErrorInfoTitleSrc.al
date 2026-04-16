/// Helper codeunit exercising ErrorInfo.Title() get/set.
codeunit 83800 "EIT Src"
{
    /// Sets Title and returns it (round-trip test).
    procedure SetAndGet(title: Text): Text
    var
        EI: ErrorInfo;
    begin
        EI.Title(title);
        exit(EI.Title());
    end;

    /// Returns Title on a freshly-initialised ErrorInfo (default empty).
    procedure DefaultTitle(): Text
    var
        EI: ErrorInfo;
    begin
        exit(EI.Title());
    end;

    /// Sets Title twice — last value must win.
    procedure LastWriteWins(first: Text; second: Text): Text
    var
        EI: ErrorInfo;
    begin
        EI.Title(first);
        EI.Title(second);
        exit(EI.Title());
    end;
}
