// Declares nothing itself. Everything its audit line names comes from PTM Plain Ext.
page 70662 "PTM Plain"
{
    PageType = List;
    SourceTable = "PTM Row";
    layout { area(Content) { repeater(Rows) { field("No."; Rec."No.") { ApplicationArea = All; } } } }
}
