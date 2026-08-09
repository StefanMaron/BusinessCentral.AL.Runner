/// The page whose id a pageextension in this bundle deliberately reuses (#1710).
page 64223 "EOM Card"
{
    PageType = Card;
    SourceTable = "EOM Item";

    layout
    {
        area(content)
        {
            field(CodeField; Rec."Code") { ApplicationArea = All; }
            field(DescField; Rec."Description") { ApplicationArea = All; }
        }
    }
}

/// A second page, extended by `pageextension 64223` below so that the id collision is
/// between objects that genuinely have nothing to do with each other.
page 64224 "EOM Other"
{
    PageType = Card;
    SourceTable = "EOM Item";

    layout
    {
        area(content)
        {
            field(OtherCodeField; Rec."Code") { ApplicationArea = All; }
        }
    }
}
