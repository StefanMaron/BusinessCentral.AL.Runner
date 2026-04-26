codeunit 50907 "Document Line Tests"
{
    Subtype = Test;

    var
        DocLineMgmt: Codeunit "Document Line Management";
        Assert: Codeunit Assert;

    [Test]
    procedure TestInsertAndGetWithCompositeKey()
    var
        DocLine: Record "Test Document Line";
    begin
        // [GIVEN] A document line with composite PK (Document No. + Line No.)
        DocLine.Init();
        DocLine."Document No." := 'ORD-001';
        DocLine."Line No." := 10000;
        DocLine."Item No." := 'ITEM-A';
        DocLine."Quantity" := 5;
        DocLine."Unit Price" := 10.00;
        DocLine."Amount" := 50.00;
        DocLine.Insert(true);

        // [WHEN] Getting the record by composite key
        DocLine.Init();
        DocLine.Get('ORD-001', 10000);

        // [THEN] All fields should match
        Assert.AreEqual('ITEM-A', DocLine."Item No.", 'Item No. should match after Get');
        Assert.AreEqual(5, DocLine."Quantity", 'Quantity should match after Get');
        Assert.AreEqual(50, DocLine."Amount", 'Amount should match after Get');
    end;

    [Test]
    procedure TestMultipleLinesPerDocument()
    var
        Total: Decimal;
    begin
        // [GIVEN] Three lines under the same document
        CreateDocLine('ORD-010', 10000, 'ITEM-X', 2, 25.00, 50.00);
        CreateDocLine('ORD-010', 20000, 'ITEM-Y', 1, 75.00, 75.00);
        CreateDocLine('ORD-010', 30000, 'ITEM-Z', 3, 10.00, 30.00);

        // [WHEN] Calculating the document total
        Total := DocLineMgmt.GetDocumentTotal('ORD-010');

        // [THEN] Total should be sum of all line amounts
        Assert.AreEqual(155, Total, 'Document total should be 50 + 75 + 30 = 155');
    end;

    [Test]
    procedure TestLineCountPerDocument()
    var
        Count: Integer;
    begin
        // [GIVEN] Lines across two different documents
        CreateDocLine('ORD-020', 10000, 'ITEM-A', 1, 10.00, 10.00);
        CreateDocLine('ORD-020', 20000, 'ITEM-B', 1, 20.00, 20.00);
        CreateDocLine('ORD-021', 10000, 'ITEM-C', 1, 30.00, 30.00);

        // [WHEN] Counting lines for ORD-020
        Count := DocLineMgmt.GetLineCount('ORD-020');

        // [THEN] Should only count lines for that document
        Assert.AreEqual(2, Count, 'ORD-020 should have 2 lines');
    end;

    [Test]
    procedure TestModifyLineWithCompositeKey()
    var
        DocLine: Record "Test Document Line";
    begin
        // [GIVEN] An existing document line
        CreateDocLine('ORD-030', 10000, 'ITEM-M', 3, 20.00, 60.00);

        // [WHEN] Modifying quantity and recalculating amount
        DocLine.Get('ORD-030', 10000);
        DocLine."Quantity" := 7;
        DocLine.Modify();
        DocLineMgmt.UpdateAmount('ORD-030', 10000);

        // [THEN] Amount should be recalculated
        DocLine.Get('ORD-030', 10000);
        Assert.AreEqual(140, DocLine."Amount", 'Amount should be 7 * 20 = 140');
    end;

    local procedure CreateDocLine(DocNo: Code[20]; LineNo: Integer; ItemNo: Code[20]; Qty: Integer; Price: Decimal; Amount: Decimal)
    var
        DocLine: Record "Test Document Line";
    begin
        DocLine.Init();
        DocLine."Document No." := DocNo;
        DocLine."Line No." := LineNo;
        DocLine."Item No." := ItemNo;
        DocLine."Quantity" := Qty;
        DocLine."Unit Price" := Price;
        DocLine."Amount" := Amount;
        DocLine.Insert(true);
    end;
}
