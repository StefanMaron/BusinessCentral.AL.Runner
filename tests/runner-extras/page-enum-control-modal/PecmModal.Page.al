page 64512 "Pecm Modal"
{
    // Issue #1896: a page-global control bound to an Enum-typed variable must not block
    // RunModal's form materialisation.
    PageType = List;
    SourceTable = "Pecm Row";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            field(KindSelector; SelectedKind)
            {
                ApplicationArea = All;
                Caption = 'Kind';

                trigger OnValidate()
                var
                    Echo: Record "Pecm Row";
                begin
                    if not Echo.Get('KIND') then begin
                        Echo.Init();
                        Echo."No." := 'KIND';
                        Echo.Insert();
                    end;
                end;
            }
            repeater(Rows)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
            }
        }
    }

    procedure GetSelectedKindOrdinal(): Integer
    begin
        exit(SelectedKind.AsInteger());
    end;

    var
        SelectedKind: Enum "Pecm Kind";
}
