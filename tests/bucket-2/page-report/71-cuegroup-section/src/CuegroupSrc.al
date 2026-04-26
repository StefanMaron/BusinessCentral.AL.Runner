table 58900 "CGS Sales KPIs"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; PeriodStart; Date) { }
        field(2; OpenOrders; Integer) { }
        field(3; ShippedToday; Integer) { }
        field(4; TotalRevenue; Decimal) { }
    }
    keys
    {
        key(PK; PeriodStart) { Clustered = true; }
    }
}

/// RoleCenter page containing a cuegroup section — the most common place
/// for cuegroup declarations in BC. The cuegroup defines KPI tile layout
/// which has no runtime effect in a unit-test context.
page 58900 "CGS Sales Role Center"
{
    PageType = RoleCenter;

    layout
    {
        area(RoleCenter)
        {
            group(SalesTiles)
            {
                cuegroup(OrderCues)
                {
                    field(OpenOrdersField; 0)
                    {
                        ApplicationArea = All;
                    }
                    field(ShippedField; 0)
                    {
                        ApplicationArea = All;
                    }
                }
            }
        }
    }
}

/// Card page with a cuegroup in a FactBox area — another common pattern.
page 58901 "CGS KPI FactBox"
{
    PageType = CardPart;
    SourceTable = "CGS Sales KPIs";

    layout
    {
        area(Content)
        {
            cuegroup(KPITiles)
            {
                field(OpenOrders; Rec.OpenOrders)
                {
                    ApplicationArea = All;
                }
                field(ShippedToday; Rec.ShippedToday)
                {
                    ApplicationArea = All;
                }
                field(TotalRevenue; Rec.TotalRevenue)
                {
                    ApplicationArea = All;
                }
            }
        }
    }
}

/// Business logic helper — proves that compilation unit containing pages
/// with cuegroup sections compiles and executes logic correctly.
codeunit 58900 "CGS KPI Helper"
{
    procedure CalcKPIScore(OpenOrders: Integer; Shipped: Integer): Decimal
    var
        Total: Integer;
    begin
        Total := OpenOrders + Shipped;
        if Total = 0 then
            exit(0);
        exit(Round(Shipped / Total * 100, 1));
    end;

    procedure IsHighActivity(OpenOrders: Integer): Boolean
    begin
        exit(OpenOrders > 100);
    end;
}
