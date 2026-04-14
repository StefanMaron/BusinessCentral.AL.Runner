codeunit 50301 "Test Report Dataset Columns"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ReportIdIsResolvable()
    var
        Helper: Codeunit "Report Helper";
    begin
        // Positive: the report object compiles and its ID is accessible
        Assert.AreNotEqual(0, Helper.GetReportId(), 'Report ID should be non-zero');
    end;

    [Test]
    procedure HelperLogicWorks()
    var
        Helper: Codeunit "Report Helper";
    begin
        // Positive: codeunit logic compiled alongside report is functional
        Assert.AreEqual(7, Helper.Add(3, 4), 'Expected 3+4=7');
    end;

    [Test]
    procedure HelperLogicCatchesWrongResult()
    var
        Helper: Codeunit "Report Helper";
    begin
        // Negative: verify assertion detects incorrect values
        asserterror Assert.AreEqual(99, Helper.Add(3, 4), 'Wrong sum');
        Assert.ExpectedError('Assert.AreEqual failed');
    end;

    [Test]
    procedure TableInsertAndRead()
    var
        Cust: Record "Test Customer";
    begin
        // Positive: table defined alongside report works
        Cust."No." := 'C001';
        Cust.Name := 'Contoso';
        Cust.Insert(true);

        Cust.Get('C001');
        Assert.AreEqual('Contoso', Cust.Name, 'Customer name mismatch');
    end;
}
