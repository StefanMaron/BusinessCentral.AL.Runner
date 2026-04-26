codeunit 167003 "NSU Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure FindSet_NavScopeUsing_Compiles()
    var
        Src: Codeunit "NSU Source";
    begin
        // Positive: SumPayments compiles and runs even with no records
        Assert.AreEqual(0, Src.SumPayments(), 'Empty table should sum to 0');
    end;

    [Test]
    procedure FindSet_NavScopeUsing_SumsCorrectly()
    var
        Src: Codeunit "NSU Source";
    begin
        // Positive: FindSet in a loop returns the correct non-zero sum (non-default value)
        Src.InsertPayment('P001', 100);
        Src.InsertPayment('P002', 250);
        Assert.AreEqual(350, Src.SumPayments(), 'Should sum to 350');
    end;
}
