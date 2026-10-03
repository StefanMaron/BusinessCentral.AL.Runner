/// <summary>
/// A test library between the app and its tests. "Calc" reaches "Missing" on the app's codeunit,
/// which the app does not declare, through "Step"; "Other" reaches nothing missing. The test
/// bundle's own compile never sees the call, because the library is a dependency of it.
/// </summary>
codeunit 65450 "Lib Bundle Helper"
{
    procedure Calc(): Integer
    begin
        exit(Step());
    end;

    procedure Other(): Integer
    begin
        exit(7);
    end;

    local procedure Step(): Integer
    var
        Loyalty: Codeunit "Lib Bundle Loyalty";
        Result: Integer;
    begin
        Result := Loyalty.Missing(1);
        exit(Result);
    end;
}
