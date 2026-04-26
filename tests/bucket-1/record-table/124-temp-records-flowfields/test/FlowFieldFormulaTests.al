codeunit 56242 "FlowField Formula Tests"
{
    Subtype = Test;

    var Assert: Codeunit Assert;

    [Test]
    procedure CountFlowFieldReturnsCorrectCount()
    var
        Header: Record "FF Order Header";
        Line: Record "FF Order Line";
    begin
        Header.Init();
        Header."No." := 'ORD001';
        Header.Insert();

        Line.Init();
        Line."Order No." := 'ORD001';
        Line."Line No." := 10000;
        Line."Item No." := 'ITEM-A';
        Line.Amount := 100.0;
        Line.Insert();

        Line.Init();
        Line."Order No." := 'ORD001';
        Line."Line No." := 20000;
        Line."Item No." := 'ITEM-B';
        Line.Amount := 250.50;
        Line.Insert();

        Header.Get('ORD001');
        Header.CalcFields("Line Count");
        Assert.AreEqual(2, Header."Line Count", 'Count FlowField should return 2 lines');
    end;

    [Test]
    procedure SumFlowFieldReturnsCorrectTotal()
    var
        Header: Record "FF Order Header";
        Line: Record "FF Order Line";
    begin
        Header.Init();
        Header."No." := 'ORD002';
        Header.Insert();

        Line.Init();
        Line."Order No." := 'ORD002';
        Line."Line No." := 10000;
        Line.Amount := 100.00;
        Line.Insert();

        Line.Init();
        Line."Order No." := 'ORD002';
        Line."Line No." := 20000;
        Line.Amount := 250.50;
        Line.Insert();

        Header.Get('ORD002');
        Header.CalcFields("Total Amount");
        Assert.AreEqual(350.50, Header."Total Amount", 'Sum FlowField should return 350.50');
    end;

    [Test]
    procedure LookupFlowFieldReturnsFirstMatch()
    var
        Header: Record "FF Order Header";
        Line: Record "FF Order Line";
    begin
        Header.Init();
        Header."No." := 'ORD003';
        Header.Insert();

        Line.Init();
        Line."Order No." := 'ORD003';
        Line."Line No." := 10000;
        Line."Item No." := 'FIRST-ITEM';
        Line.Insert();

        Line.Init();
        Line."Order No." := 'ORD003';
        Line."Line No." := 20000;
        Line."Item No." := 'SECOND-ITEM';
        Line.Insert();

        Header.Get('ORD003');
        Header.CalcFields("First Item");
        Assert.AreEqual('FIRST-ITEM', Header."First Item", 'Lookup FlowField should return first matching item');
    end;

    [Test]
    procedure CountFlowFieldReturnsZeroForNoLines()
    var
        Header: Record "FF Order Header";
    begin
        Header.Init();
        Header."No." := 'ORD-EMPTY';
        Header.Insert();

        Header.CalcFields("Line Count");
        Assert.AreEqual(0, Header."Line Count", 'Count should be 0 when no lines exist');
    end;

    [Test]
    procedure SumFlowFieldReturnsZeroForNoLines()
    var
        Header: Record "FF Order Header";
    begin
        Header.Init();
        Header."No." := 'ORD-EMPTY2';
        Header.Insert();

        Header.CalcFields("Total Amount");
        Assert.AreEqual(0.0, Header."Total Amount", 'Sum should be 0 when no lines exist');
    end;

    [Test]
    procedure FlowFieldsFilterByParentKey()
    var
        Header1: Record "FF Order Header";
        Header2: Record "FF Order Header";
        Line: Record "FF Order Line";
    begin
        Header1.Init();
        Header1."No." := 'H1';
        Header1.Insert();

        Header2.Init();
        Header2."No." := 'H2';
        Header2.Insert();

        Line.Init();
        Line."Order No." := 'H1';
        Line."Line No." := 10000;
        Line.Amount := 500.00;
        Line.Insert();

        Header1.CalcFields("Line Count", "Total Amount");
        Assert.AreEqual(1, Header1."Line Count", 'H1 should have 1 line');
        Assert.AreEqual(500.00, Header1."Total Amount", 'H1 total should be 500');

        Header2.CalcFields("Line Count", "Total Amount");
        Assert.AreEqual(0, Header2."Line Count", 'H2 should have 0 lines');
        Assert.AreEqual(0.0, Header2."Total Amount", 'H2 total should be 0');
    end;

    [Test]
    procedure DuplicateLineInsertThrowsError()
    var
        Header: Record "FF Order Header";
        Line: Record "FF Order Line";
    begin
        Header.Init();
        Header."No." := 'DUP-HDR';
        Header.Insert();

        Line.Init();
        Line."Order No." := 'DUP-HDR';
        Line."Line No." := 10000;
        Line.Amount := 50.00;
        Line.Insert();

        asserterror begin
            Line.Init();
            Line."Order No." := 'DUP-HDR';
            Line."Line No." := 10000;
            Line.Amount := 75.00;
            Line.Insert();
        end;
        Assert.ExpectedError('already exists');
    end;
}
