page 65500 "ALR Bundle Salesperson Page"
{
    // Regression fixture for issue #2452: a page COMPILED FROM THIS BUNDLE'S OWN AL SOURCE
    // whose SourceTable property names a table that ships PRECOMPILED in a loaded dependency
    // .app (Base Application "Salesperson/Purchaser", table 13) — not a table this bundle
    // itself declares. "Salesperson/Purchaser" is a plain Code+Name table with no OnValidate
    // reaching into No. Series setup (unlike "Resource", whose "No." field does — and No.
    // Series setup differs across supported BC versions on an unconfigured/empty database),
    // so this fixture proves the SourceTable resolution fix cleanly.
    PageType = Card;
    SourceTable = "Salesperson/Purchaser";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            field(Code; Rec.Code) { ApplicationArea = All; }
            field(Name; Rec.Name) { ApplicationArea = All; }
        }
    }
}
