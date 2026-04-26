/// Tests that Page<N> class has RunModal() and LookupMode injected as members.
/// Issue #1079: CS1061 on 'Page<N>': missing 'LookupMode', 'RunModal'.
///
/// When AL code inside a page trigger uses CurrPage.LookupMode or CurrPage.RunModal(),
/// BC generates code that calls LookupMode/RunModal on the Page<N> class directly
/// (via CurrPage => (Page<N>)this). Without injection these fail with CS1061.

table 60490 "PCM Source"
{
    fields
    {
        field(1; "Id"; Integer) { }
    }
    keys { key(PK; "Id") { Clustered = true; } }
}

/// A page that sets CurrPage.LookupMode in its OnOpenPage trigger.
/// BC generates: _parent.CurrPage.LookupMode = true;
/// where CurrPage returns (Page60490)this — so Page60490.LookupMode must exist.
page 60490 "PCM Card"
{
    PageType = Card;
    SourceTable = "PCM Source";

    layout
    {
        area(Content)
        {
            field(Id; Rec."Id") { ApplicationArea = All; }
        }
    }

    trigger OnOpenPage()
    begin
        /// CurrPage.LookupMode accesses this.LookupMode on the Page<N> class directly.
        /// Without the LookupMode property injected, BC generates CS1061.
        CurrPage.LookupMode := true;
    end;
}

/// A second page that calls CurrPage.RunModal() in its trigger.
/// BC generates: _parent.CurrPage.RunModal();
/// where CurrPage returns (Page60491)this — so Page60491.RunModal() must exist.
page 60491 "PCM RunModal"
{
    PageType = Card;
    SourceTable = "PCM Source";

    layout
    {
        area(Content)
        {
            field(Id; Rec."Id") { ApplicationArea = All; }
        }
    }

    trigger OnOpenPage()
    begin
        /// CurrPage.RunModal() calls RunModal() on the Page<N> class directly.
        /// Without RunModal() injected, BC generates CS1061.
        CurrPage.RunModal();
    end;
}

/// Helper codeunit: returns true to confirm the pages compiled without CS1061.
codeunit 60490 "PCM Src"
{
    /// Returns true to confirm the LookupMode page compiled.
    procedure LookupModePageCompiles(): Boolean
    begin
        exit(true);
    end;

    /// Returns true to confirm the RunModal page compiled.
    procedure RunModalPageCompiles(): Boolean
    begin
        exit(true);
    end;
}
