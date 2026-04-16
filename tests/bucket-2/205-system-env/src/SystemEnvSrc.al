/// Exercises System environment/session stubs: GuiAllowed, WorkDate,
/// GlobalLanguage, WindowsLanguage, RoundDateTime, IsNull.
codeunit 60330 "SEV Src"
{
    procedure IsGui(): Boolean
    begin
        exit(GuiAllowed());
    end;

    procedure GetWorkDate(): Date
    begin
        exit(WorkDate());
    end;

    procedure SetAndGetWorkDate(d: Date): Date
    begin
        WorkDate(d);
        exit(WorkDate());
    end;

    procedure GetGlobalLang(): Integer
    begin
        exit(GlobalLanguage());
    end;

    procedure GetWindowsLang(): Integer
    begin
        exit(WindowsLanguage());
    end;

    procedure RoundDateTimePrecision(dt: DateTime; precision: BigInteger): DateTime
    begin
        exit(RoundDateTime(dt, precision));
    end;

    procedure IsNullCheck(): Boolean
    var
        v: Variant;
    begin
        // Default Variant is null-ish.
        exit(IsNull(v));
    end;
}
