/// Arm 3: an action a pageextension adds to a page that ships PRECOMPILED inside Base
/// Application (#1923's "silent no-op" symptom — the more dangerous half: nothing threw at
/// Invoke() time at all, so a test only caught the miss one step later on the effect the
/// action was supposed to have).
pageextension 64523 "Pad Item Attr Ext" extends "Item Attributes"
{
    actions
    {
        addlast(Processing)
        {
            action(ExtActionOnBaseAppPage)
            {
                ApplicationArea = All;
                Caption = 'Ext Action On Base App Page';

                trigger OnAction()
                var
                    Row: Record "Pad Row";
                begin
                    Row.Log('EXT-BASEAPP-PAGE');
                end;
            }
        }
    }
}
