codeunit 50100 CalcTest
{
    Subtype = Test;

    [Test]
    procedure ComputeDoubles()
    var
        Sut: Codeunit Calc;
    begin
        if Sut.Compute(3) <> 6 then Error('expected 6');
    end;

    [Test]
    procedure FailingTest()
    var
        Sut: Codeunit Calc;
    begin
        if Sut.Compute(1) <> 99 then Error('intentional failure');
    end;
}
