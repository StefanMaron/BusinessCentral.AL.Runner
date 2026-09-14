/// Both controls sit in ONE addlast(Content) block, differing only in what they bind to. That
/// pairing is the discriminator corpus 60978 established on a real service tier: if extension
/// controls never reached the page metadata, NEITHER would resolve. BC finds both.
pageextension 65983 "Pcse Card Ext" extends "Pcse Card"
{
    layout
    {
        addlast(Content)
        {
            field(ExtGlobalField; ExtVar)
            {
                ApplicationArea = All;
                Caption = 'Ext Global Field';
            }
            field(ExtRecField; Rec."Rec Text")
            {
                ApplicationArea = All;
                Caption = 'Ext Rec Field';
            }
            // #3228's Label-bound shape. #3228 diagnosed it as "the extension's controls are not
            // in the page metadata at all"; corpus #359 ruled that out, and it shares this
            // issue's cause -- the extension's OnMetadataLoaded not having run yet.
            // #3228's OTHER shape, two controls over ONE extension variable, is deliberately not
            // here: it still fails after this fix, for a different reason (BC registers one
            // source expression per binding, not per control), and stays with #3228.
            field(ExtLabelField; ExtLbl)
            {
                ApplicationArea = All;
                Editable = false;
            }
        }
    }

    trigger OnOpenPage()
    begin
        ExtVar := 'ext';
    end;

    var
        ExtVar: Text[30];
        ExtLbl: Label 'Ext label';
}
