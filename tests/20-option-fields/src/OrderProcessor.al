codeunit 50120 "Order Processor"
{
    procedure CreateOrder(OrderNo: Code[20]; Description: Text[100])
    var
        Ord: Record "Demo Order";
    begin
        Ord.Init();
        Ord."Order No." := OrderNo;
        Ord."Description" := Description;
        Ord."Status" := Ord."Status"::Draft;
        Ord.Insert(true);
    end;

    procedure ApproveOrder(OrderNo: Code[20])
    var
        Ord: Record "Demo Order";
    begin
        Ord.Get(OrderNo);
        Ord."Status" := Ord."Status"::Approved;
        Ord.Modify();
    end;

    procedure RejectOrder(OrderNo: Code[20])
    var
        Ord: Record "Demo Order";
    begin
        Ord.Get(OrderNo);
        Ord."Status" := Ord."Status"::Rejected;
        Ord.Modify();
    end;

    procedure GetStatus(OrderNo: Code[20]): Enum "Order Status"
    var
        Ord: Record "Demo Order";
    begin
        Ord.Get(OrderNo);
        exit(Ord."Status");
    end;

    procedure IsFinalized(OrderNo: Code[20]): Boolean
    var
        Ord: Record "Demo Order";
    begin
        Ord.Get(OrderNo);
        exit((Ord."Status" = Ord."Status"::Approved) or (Ord."Status" = Ord."Status"::Rejected));
    end;
}
