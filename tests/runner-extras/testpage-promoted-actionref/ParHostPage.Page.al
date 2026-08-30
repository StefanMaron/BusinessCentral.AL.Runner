/// The page under test. Every action that carries a trigger lives in area(Processing); every
/// actionref lives in area(Promoted) and delegates to one of them.
///
/// `TriggerlessAction` is the negative control: a RunObject action genuinely has no OnAction
/// trigger, so both invoking it directly and invoking the actionref that points at it must
/// keep raising the loud testpage-action refusal. Without it, a fix that resolved "no trigger
/// found" to "run nothing, quietly" would pass every positive arm below.
page 64541 "Par Host Page"
{
    PageType = List;
    SourceTable = "Par Row";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(FlatTarget)
            {
                ApplicationArea = All;
                Caption = 'Flat Target';

                trigger OnAction()
                var
                    Row: Record "Par Row";
                begin
                    Row.Log('FLAT');
                end;
            }

            group(Grouped)
            {
                Caption = 'Grouped';

                action(GroupedTarget)
                {
                    ApplicationArea = All;
                    Caption = 'Grouped Target';

                    trigger OnAction()
                    var
                        Row: Record "Par Row";
                    begin
                        Row.Log('GROUPED');
                    end;
                }
            }

            action(BaseTargetForExt)
            {
                ApplicationArea = All;
                Caption = 'Base Target For Ext';

                trigger OnAction()
                var
                    Row: Record "Par Row";
                begin
                    Row.Log('BASE-VIA-EXT');
                end;
            }

            action(NeverInvokedTarget)
            {
                ApplicationArea = All;
                Caption = 'Never Invoked Target';

                trigger OnAction()
                var
                    Row: Record "Par Row";
                begin
                    Row.Log('NEVER');
                end;
            }

            action(TriggerlessAction)
            {
                ApplicationArea = All;
                Caption = 'Triggerless Action';
                RunObject = page "Par Host Page";
            }
        }

        area(Promoted)
        {
            actionref(FlatRef; FlatTarget) { }

            group(Category_Process)
            {
                Caption = 'Process';

                actionref(GroupedRef; GroupedTarget) { }
            }

            actionref(TriggerlessRef; TriggerlessAction) { }
        }
    }
}
