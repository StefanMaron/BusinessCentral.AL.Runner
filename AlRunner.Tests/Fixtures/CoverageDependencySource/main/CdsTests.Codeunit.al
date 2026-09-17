codeunit 70870 "CDS Tests"
{
    Subtype = Test;

    [Test]
    procedure DependencyMethodExecutes()
    var
        Subject: Codeunit "CDS Subject";
        Actual: Integer;
    begin
        Actual := Subject.Twice(21);
        if Actual <> 42 then
            Error('expected 42, actual %1', Actual);
    end;
}
