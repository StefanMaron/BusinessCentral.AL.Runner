/// Helper codeunit exercising ErrorInfo.ControlName() get/set.
codeunit 81300 "EICN Src"
{
    /// Sets ControlName and returns it (round-trip test).
    procedure SetAndGet(): Text
    var
        EI: ErrorInfo;
    begin
        EI.ControlName('MyField');
        exit(EI.ControlName());
    end;

    /// Returns ControlName on a freshly-initialised ErrorInfo (default).
    procedure DefaultControlName(): Text
    var
        EI: ErrorInfo;
    begin
        exit(EI.ControlName());
    end;

    /// Sets ControlName to empty string.
    procedure SetEmpty(): Text
    var
        EI: ErrorInfo;
    begin
        EI.ControlName('');
        exit(EI.ControlName());
    end;

    /// Sets two different ControlNames; returns the second.
    procedure OverwriteControlName(): Text
    var
        EI: ErrorInfo;
    begin
        EI.ControlName('First');
        EI.ControlName('Second');
        exit(EI.ControlName());
    end;
}
