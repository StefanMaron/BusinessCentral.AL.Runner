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

            // Issue #1966: dispatch alone is not enough — the trigger body must be able to
            // READ Rec and see the page's actual current row. #1954's four original tests in
            // this suite all logged a fixed tag and never touched Rec, so they stayed green
            // while get_Rec() NREd on every corpus leg (PageExtension60723.get_Rec, see
            // tests/al-language's TestPageExtensionActionInvoke_Tests.al,
            // ExtActionInvokeRunsAgainstThePagesCurrentRow — already upstream and already the
            // real service-tier proof; this is the fast local regression pin for it).
            action(ExtActionReadsRec)
            {
                ApplicationArea = All;
                Caption = 'Ext Action Reads Rec';

                trigger OnAction()
                var
                    Stamp: Record "Pad Row";
                begin
                    if not Stamp.Get('REC-STAMP') then begin
                        Stamp.Init();
                        Stamp."No." := 'REC-STAMP';
                        Stamp.Descr := Rec."No.";
                        Stamp.Insert();
                    end else begin
                        Stamp.Descr := Rec."No.";
                        Stamp.Modify();
                    end;
                end;
            }
        }
    }
}
