/// A pageextension over the SOURCE-COMPILED page above. Its two actionrefs cover the two
/// directions a promoted reference can cross an id space: `ExtRefToExtTarget` points at an
/// action this extension declares (member id hashed from the EXTENSION's object id), while
/// `ExtRefToBaseTarget` points at an action the BASE page declares (member id hashed from the
/// BASE PAGE's object id). A fix that only re-derived the target id in one id space passes one
/// of these two and fails the other.
pageextension 64542 "Par Host Page Ext" extends "Par Host Page"
{
    actions
    {
        addlast(Processing)
        {
            action(ExtTarget)
            {
                ApplicationArea = All;
                Caption = 'Ext Target';

                trigger OnAction()
                var
                    Row: Record "Par Row";
                begin
                    Row.Log('EXT');
                end;
            }
        }

        addlast(Promoted)
        {
            actionref(ExtRefToExtTarget; ExtTarget) { }
            actionref(ExtRefToBaseTarget; BaseTargetForExt) { }
        }
    }
}
