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

    [Test]
    procedure ConditionalBranchExercises()
    var
        Sut: Codeunit Calc;
        n: Integer;
    begin
        n := Sut.Compute(2);
        if n > 0 then begin
            n := n + 1;
            n := n * 2;
        end;
        if n <> 10 then Error('expected 10');
    end;
}
