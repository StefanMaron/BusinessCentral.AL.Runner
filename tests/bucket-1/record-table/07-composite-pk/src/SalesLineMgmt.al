codeunit 50107 "Document Line Management"
{
    procedure UpdateAmount(DocNo: Code[20]; LineNo: Integer)
    var
        DocLine: Record "Test Document Line";
    begin
        DocLine.Get(DocNo, LineNo);
        DocLine."Amount" := DocLine."Quantity" * DocLine."Unit Price";
        DocLine.Modify();
    end;

    procedure GetDocumentTotal(DocNo: Code[20]): Decimal
    var
        DocLine: Record "Test Document Line";
        Total: Decimal;
    begin
        DocLine.SetRange("Document No.", DocNo);
        if DocLine.FindSet() then
            repeat
                Total += DocLine."Amount";
            until DocLine.Next() = 0;
        exit(Total);
    end;

    procedure GetLineCount(DocNo: Code[20]): Integer
    var
        DocLine: Record "Test Document Line";
    begin
        DocLine.SetRange("Document No.", DocNo);
        exit(DocLine.Count());
    end;
}
