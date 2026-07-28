/// <summary>
/// Members live on a separate object, not on the field that uses it — which is the whole
/// difference this suite is about.
/// </summary>
enum 62130 "TEF Grade"
{
    Extensible = false;

    // Captions are deliberately identical to the member names. Whether BC's TestPage resolves a
    // member by name or by caption when the two differ is a real question, but it is not one this
    // suite has verified against a service tier, so it asserts nothing about it.
    value(0; Low) { Caption = 'Low'; }
    value(1; Mid) { Caption = 'Mid'; }
    value(2; High) { Caption = 'High'; }
}

table 62130 "TEF Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        // The control. Same runtime type as Grade, but its members are written on the field, so a
        // runner reading members off the field alone still answers this one correctly.
        field(2; Kind; Option) { OptionMembers = Alpha,Beta,Gamma; }
        // The subject. Non-zero target values throughout, so "the default happened to be right"
        // is never an explanation for a green result.
        field(3; Grade; Enum "TEF Grade") { }
        // Not an option at all. Its only job is to answer "does an edit to an existing row reach
        // the table?" independently of anything to do with option members.
        field(4; Note; Text[30]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

page 62130 "TEF Card"
{
    PageType = Card;
    SourceTable = "TEF Row";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            group(General)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Kind; Rec.Kind) { ApplicationArea = All; }
                field(Grade; Rec.Grade) { ApplicationArea = All; }
                field(Note; Rec.Note) { ApplicationArea = All; }
            }
        }
    }
}
