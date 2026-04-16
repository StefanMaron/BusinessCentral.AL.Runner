/// Tables and helper codeunit exercising Record.SetAutoCalcFields.
table 61200 "SACF Order"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; "Line Count"; Integer)
        {
            FieldClass = FlowField;
            CalcFormula = count("SACF Line" where("Order No." = field("No.")));
        }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

table 61201 "SACF Line"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "Order No."; Code[20]) { }
        field(2; "Line No."; Integer) { }
    }
    keys { key(PK; "Order No.", "Line No.") { Clustered = true; } }
}

codeunit 61202 "SACF Helper"
{
    procedure InsertOrder(orderNo: Code[20])
    var
        Order: Record "SACF Order";
    begin
        Order.Init();
        Order."No." := orderNo;
        Order.Insert();
    end;

    procedure InsertLines(orderNo: Code[20]; lineCount: Integer)
    var
        Line: Record "SACF Line";
        i: Integer;
    begin
        for i := 1 to lineCount do begin
            Line.Init();
            Line."Order No." := orderNo;
            Line."Line No." := i;
            Line.Insert();
        end;
    end;

    /// Returns Line Count after SetAutoCalcFields + Get.
    procedure GetLineCountWithAutoCalc(orderNo: Code[20]): Integer
    var
        Order: Record "SACF Order";
    begin
        Order.SetAutoCalcFields("Line Count");
        Order.Get(orderNo);
        exit(Order."Line Count");
    end;

    /// Returns Line Count after a plain Get (no SetAutoCalcFields).
    procedure GetLineCountWithoutAutoCalc(orderNo: Code[20]): Integer
    var
        Order: Record "SACF Order";
    begin
        Order.Get(orderNo);
        exit(Order."Line Count");
    end;

    /// Returns Line Count after SetAutoCalcFields + FindFirst.
    procedure FindFirstLineCountWithAutoCalc(orderNo: Code[20]): Integer
    var
        Order: Record "SACF Order";
    begin
        Order.SetAutoCalcFields("Line Count");
        Order.SetRange("No.", orderNo);
        Order.FindFirst();
        exit(Order."Line Count");
    end;
}
