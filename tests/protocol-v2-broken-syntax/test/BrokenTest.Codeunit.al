codeunit 50301 BrokenTest
{
    Subtype = Test;

    [Test]
    procedure NeverRuns()
    var
        Sut: Codeunit Broken;
    begin
        if Sut.Compute(2) <> 4 then Error('expected 4');
    end;
}
