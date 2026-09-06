// A list page whose PurgeAll control binds to a page GLOBAL variable, not to a field of the
// source table — the same binding shape as page 9816 "Permission Set by User"'s
// AllUsersHavePermission, which is the control issue #3105 reported.
//
// The control's own OnValidate raises nothing. It calls Delete(true), and the refusal comes
// from "TSR Guard"'s OnBeforeDeleteEvent subscriber underneath that.
page 70603 "TSR Page"
{
    PageType = List;
    SourceTable = "TSR Row";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            field(PurgeAll; PurgeAll)
            {
                ApplicationArea = All;
                Caption = 'Purge All';

                trigger OnValidate()
                var
                    Row: Record "TSR Row";
                begin
                    if not PurgeAll then
                        exit;
                    if Row.FindSet() then
                        repeat
                            Row.Delete(true);
                        until Row.Next() = 0;
                end;
            }
            repeater(Rows)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Guarded; Rec.Guarded) { ApplicationArea = All; }
            }
        }
    }

    var
        PurgeAll: Boolean;
}
