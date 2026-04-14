codeunit 50122 "Persistence Helper"
{
    procedure SetupData()
    var
        Rec: Record "Persistence Demo";
    begin
        Rec.Init();
        Rec."Entry No." := 1;
        Rec."Description" := 'First Entry';
        Rec."Amount" := 100;
        Rec.Insert(true);

        Rec.Init();
        Rec."Entry No." := 2;
        Rec."Description" := 'Second Entry';
        Rec."Amount" := 250;
        Rec.Insert(true);

        Rec.Init();
        Rec."Entry No." := 3;
        Rec."Description" := 'Third Entry';
        Rec."Amount" := 50;
        Rec.Insert(true);
    end;

    procedure GetTotalAmount(): Decimal
    var
        Rec: Record "Persistence Demo";
        Total: Decimal;
    begin
        if Rec.FindSet() then
            repeat
                Total += Rec."Amount";
            until Rec.Next() = 0;
        exit(Total);
    end;

    procedure GetRecordCount(): Integer
    var
        Rec: Record "Persistence Demo";
    begin
        exit(Rec.Count());
    end;

    procedure GetDescription(EntryNo: Integer): Text[100]
    var
        Rec: Record "Persistence Demo";
    begin
        Rec.Get(EntryNo);
        exit(Rec."Description");
    end;
}
