/// <summary>
/// The second of two files calling the same missing member with shapes that disagree (a Boolean
/// result; A6 assigns an Integer). The stub has the first file's shape, so this call no longer
/// compiles and its test is FAILED naming the mismatch, not run against a stub of its own.
/// </summary>
codeunit 65329 "Precompiled Order Second"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure DisagreeingShapes_TheSecondFileLoses()
    var
        Points: Codeunit "Precompiled Points";
        Flag: Boolean;
    begin
        Flag := Points.Pick(1);
    end;
}
