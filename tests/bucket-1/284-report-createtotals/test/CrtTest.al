/// Tests proving CurrReport.CreateTotals() is a no-op stub in standalone mode (issue #991).
codeunit 140002 "CRT Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure CreateTotals_ZeroArg_NoThrow()
    var
        Src: Codeunit "CRT Source";
    begin
        // Positive: CurrReport.CreateTotals() (0-arg) in OnPreReport must not throw.
        // OnPreReport fires unconditionally, so this always exercises the stub.
        Src.RunReport_NoThrow();
        Assert.IsTrue(true, 'CreateTotals() must not throw in standalone mode');
    end;
}
