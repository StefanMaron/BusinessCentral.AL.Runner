codeunit 313201 "PRB Demo Test"
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    [Test]
    procedure CallPureLogicProcedure_Works()
    var
        DemoPage: Page "PRB Demo Page";
        Result: Integer;
    begin
        // Pure-logic procedure — does not touch Rec. Must already work.
        Result := DemoPage.Echo(41);
        Assert.AreEqual(42, Result, 'Page.Echo(41) should return 42');
    end;

    [Test]
    procedure CountRows_OnEmptyStore_ReturnsZero()
    var
        DemoPage: Page "PRB Demo Page";
        Tbl: Record "PRB Demo Tbl";
        n: Integer;
    begin
        // Prove Rec is initialized: CountRows should not NRE, and returns 0 for empty store.
        Tbl.DeleteAll();
        n := DemoPage.CountRows();
        Assert.AreEqual(0, n, 'CountRows on empty page Rec should be 0');
    end;

    [Test]
    procedure CountRows_WithRecords_ReturnsCorrectCount()
    var
        DemoPage: Page "PRB Demo Page";
        Tbl: Record "PRB Demo Tbl";
        n: Integer;
    begin
        // Prove the backing record store is functional — not a zero-returning stub.
        Tbl.DeleteAll();
        Tbl.Init();
        Tbl."No." := 'A';
        Tbl.Amount := 10;
        Tbl.Insert();
        Tbl.Init();
        Tbl."No." := 'B';
        Tbl.Amount := 20;
        Tbl.Insert();

        n := DemoPage.CountRows();
        Assert.AreEqual(2, n, 'CountRows should return 2 after inserting 2 rows');
    end;

    [Test]
    procedure SumAmount_WithRecords_ReturnsCorrectSum()
    var
        DemoPage: Page "PRB Demo Page";
        Tbl: Record "PRB Demo Tbl";
        Total: Decimal;
    begin
        // Prove Rec iteration works end-to-end (the SetConditions/GetConditions pattern).
        Tbl.DeleteAll();
        Tbl.Init();
        Tbl."No." := 'X';
        Tbl.Amount := 100;
        Tbl.Insert();
        Tbl.Init();
        Tbl."No." := 'Y';
        Tbl.Amount := 200;
        Tbl.Insert();

        Total := DemoPage.SumAmount();
        Assert.AreEqual(300, Total, 'SumAmount should return 300 for amounts 100+200');
    end;
}
