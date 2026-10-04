// A modal page (its OK closes it), and a list page whose Edit opens the card.
page 72815 "PT Dialog"
{
    PageType = StandardDialog;
    SourceTable = "PT Quiet";
    layout { area(Content) { field(K; Rec.K) { } } }
    trigger OnQueryClosePage(CloseAction: Action): Boolean var T: Codeunit "PT Target"; R: Integer; begin R := T.MDlgQuery(1); exit(true); end;
    trigger OnClosePage() var T: Codeunit "PT Target"; R: Integer; begin R := T.MDlgClose(1); end;
}

page 72816 "PT List"
{
    PageType = List;
    SourceTable = "PT Rec";
    CardPageId = "PT Card";
    layout { area(Content) { repeater(R) { field(PK; Rec.PK) { } } } }
}

codeunit 72811 "PT Runner"
{
    procedure RunModalCard()
    begin
        Page.RunModal(Page::"PT Card");
    end;

    procedure RunNonModal()
    begin
        Page.Run(Page::"PT Card");
    end;

    procedure RunModalById(PageId: Integer)
    begin
        Page.RunModal(PageId);
    end;

    procedure RunCard()
    var P: Page "PT Card";
    begin
        P.RunModal();
    end;
}
