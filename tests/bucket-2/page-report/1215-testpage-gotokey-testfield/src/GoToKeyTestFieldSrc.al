// Source objects for issue #1215 — TestPage.GoToKey invoked with a TestField
// reference as the key value. BC emits
//   tP.ALGoToKey(DataError.ThrowError, tp2.GetField(hash))
// which previously failed to compile with CS1503: cannot convert from
// MockTestPageField to NavValue, because ALGoToKey only exposed a
// `params NavValue[]` overload.
table 1215001 "GoToKey TF Rec"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

page 1215001 "GoToKey TF List"
{
    PageType = List;
    SourceTable = "GoToKey TF Rec";
    layout
    {
        area(Content)
        {
            repeater(R)
            {
                field("No."; Rec."No.") { }
                field(Name; Rec.Name) { }
            }
        }
    }
}
