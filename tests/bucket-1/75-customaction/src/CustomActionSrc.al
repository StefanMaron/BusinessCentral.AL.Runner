table 59600 "CA Order"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; OrderNo; Code[20]) { }
        field(2; Amount; Decimal) { }
        field(3; Status; Option) { OptionMembers = Open,Released,Closed; }
    }
    keys
    {
        key(PK; OrderNo) { Clustered = true; }
    }
}

/// Card page containing customaction() declarations — used to integrate with
/// Power Automate flows or other external services. Custom actions have no
/// runtime effect in a unit-test context; this proves they do not block compilation.
page 59600 "CA Order Card"
{
    PageType = Card;
    SourceTable = "CA Order";

    layout
    {
        area(Content)
        {
            field(OrderNo; Rec.OrderNo) { ApplicationArea = All; }
            field(Amount; Rec.Amount) { ApplicationArea = All; }
            field(Status; Rec.Status) { ApplicationArea = All; }
        }
    }

    actions
    {
        area(Promoted)
        {
            customaction(SendToFlow)
            {
                ApplicationArea = All;
                CustomActionType = Flow;
                FlowId = '00000000-0000-0000-0000-000000000001';
            }
            customaction(ProcessWithTemplate)
            {
                ApplicationArea = All;
                CustomActionType = FlowTemplate;
                FlowTemplateName = 'Process Order Template';
            }
        }
    }
}

/// Business logic helper — proves that the compilation unit containing
/// a page with customaction declarations compiles and runs correctly.
codeunit 59600 "CA Order Helper"
{
    procedure CalcTax(Amount: Decimal; TaxRate: Decimal): Decimal
    begin
        exit(Round(Amount * TaxRate / 100, 0.01));
    end;

    procedure IsLargeOrder(Amount: Decimal): Boolean
    begin
        exit(Amount >= 1000);
    end;

    procedure FormatOrderRef(OrderNo: Text; Amount: Decimal): Text
    begin
        exit(OrderNo + ': ' + Format(Amount));
    end;
}
