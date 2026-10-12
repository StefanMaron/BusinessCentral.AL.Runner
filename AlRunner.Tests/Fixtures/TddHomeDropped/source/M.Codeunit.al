/// <summary>"Nothing There" is generated into this file (no app declares it), and "Total" into it, but "Broken" does not compile, so the file is dropped.</summary>
codeunit 65407 "Missing Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure CallsMissing()
    var
        M: Codeunit "Nothing There";
        X: Integer;
    begin
        X := M.Total(5);
    end;

    [Test]
    procedure Broken()
    var
        I: Integer;
    begin
        I := 'abc';
    end;
}
