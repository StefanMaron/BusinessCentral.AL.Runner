/// Arm 2: an action a pageextension adds to a page COMPILED FROM SOURCE in this same bundle
/// (#1923's "misclassified OOS" symptom — the old code threw RunnerOutOfScopeException
/// naming a valid RunObject-less action as unsupported).
pageextension 64522 "Pad Host Page Ext" extends "Pad Host Page"
{
    actions
    {
        addlast(Processing)
        {
            action(ExtActionOnOwnPage)
            {
                ApplicationArea = All;
                Caption = 'Ext Action On Own Page';

                trigger OnAction()
                var
                    Row: Record "Pad Row";
                begin
                    Row.Log('EXT-OWN-PAGE');
                end;
            }
        }
    }
}
