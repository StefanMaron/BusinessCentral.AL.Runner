/// The arm that runs against a base page shipping PRECOMPILED inside Base Application
/// ("Item Attributes"), so nothing about the fix can depend on the base page having been
/// compiled from source in this bundle: the actionref, its target and the trigger all come
/// from THIS extension, while the page the TestPage drives does not.
///
/// Measured RED before the fix: `TestPage action 157710999 on page 7500 — testpage-action —
/// the page declares no OnAction trigger for this action`, the same refusal as the
/// source-compiled arms.
pageextension 64543 "Par Item Attr Ext" extends "Item Attributes"
{
    actions
    {
        addlast(Processing)
        {
            action(BaseAppExtTarget)
            {
                ApplicationArea = All;
                Caption = 'Base App Ext Target';

                trigger OnAction()
                var
                    Row: Record "Par Row";
                begin
                    Row.Log('BASEAPP-EXT');
                end;
            }
        }

        addlast(Promoted)
        {
            actionref(BaseAppExtRef; BaseAppExtTarget) { }
        }
    }
}
