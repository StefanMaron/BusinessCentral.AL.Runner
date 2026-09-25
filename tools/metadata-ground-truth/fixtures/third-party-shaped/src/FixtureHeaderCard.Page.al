// ContextSensitiveHelpPage with no ContextSensitiveHelpUrl in the manifest is AL0543.
page 70000 "TP Fixture Header Card"
{
    PageType = Card;
    SourceTable = "TP Fixture Header";
    ApplicationArea = All;
    UsageCategory = Documents;
    ContextSensitiveHelpPage = 'tp-fixture-header';

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { }
            field(Description; Rec.Description) { }
            field(Amount; Rec.Amount) { }
        }
    }
}
