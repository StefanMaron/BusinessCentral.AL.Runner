/// <summary>"Missing" is stubbed beside the package's codeunit, in a codeunit appended to this file, but "Broken" does not compile, so the file is dropped.</summary>
codeunit 65409 "Pkg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure CallsMissing()
    var
        P: Codeunit "Pkg Calc";
        X: Integer;
    begin
        X := P.Missing(5);
    end;

    [Test]
    procedure Broken()
    var
        I: Integer;
    begin
        I := 'abc';
    end;
}
