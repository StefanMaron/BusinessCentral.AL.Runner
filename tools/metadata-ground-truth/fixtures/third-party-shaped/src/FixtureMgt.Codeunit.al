codeunit 70000 "TP Fixture Mgt."
{
    procedure Total(HeaderNo: Code[20]): Decimal
    var
        FixtureLine: Record "TP Fixture Line";
    begin
        FixtureLine.SetRange("Header No.", HeaderNo);
        FixtureLine.CalcSums(Quantity);
        exit(FixtureLine.Quantity);
    end;
}
