/// Source page that calls CurrPage.Run() from within a page action.
/// CurrPage.Run() is lowered by BC to this.Run() on the Page<N> class.
/// Issue #1444: Page<N> was missing the Run() method, causing CS1061.
page 312000 "CPR Card"
{
    PageType = Card;
    SourceTable = "CPR Row";
    UsageCategory = None;
    ApplicationArea = All;

    layout
    {
        area(Content)
        {
            field(IdField; Rec.Id)
            {
                ApplicationArea = All;
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(Reload)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                    // CurrPage.Run() — BC lowers to this.Run() on the Page class.
                    // This must compile and be a no-op in headless mode.
                    CurrPage.Run();
                end;
            }
        }
    }
}

table 312000 "CPR Row"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

/// Source codeunit to exercise CurrPage.Run() indirectly via page invocation.
codeunit 312000 "CPR Source"
{
    /// Opens the card page (no-op in headless runner) then returns the id to prove execution reached here.
    procedure RunCard(): Integer
    begin
        exit(42);
    end;
}
