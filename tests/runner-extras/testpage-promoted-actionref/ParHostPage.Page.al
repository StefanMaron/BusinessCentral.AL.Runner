/// The page under test. Every action that carries a trigger lives in area(Processing); every
/// actionref lives in area(Promoted) and delegates to one of them.
///
/// The last four actions are the negative controls, and they are what an implementation that
/// resolved "no trigger found" to "run nothing, quietly" would fail:
///
///   TriggerlessAction / TriggerlessRef  a RunObject naming a PAGE. Since #2931 the runner
///                                       PERFORMS this rather than refusing it, so with no
///                                       [PageHandler] declared it must fail with BC's own
///                                       unhandled-UI error and NOT with a runner refusal.
///   LinkedPageAction                    the same, plus RunPageLink. The runner does not apply
///                                       an action's link filters yet, and opening the page
///                                       WITHOUT them would show a different rowset than real
///                                       BC, so it refuses -- with a gap anchor.
///   ReportRunObjectAction               a RunObject naming a REPORT: in scope, not implemented.
///   NoEffectAction / NoEffectRef        neither a trigger nor a RunObject, so genuinely
///                                       nothing to run; the refusal that names the actionref's
///                                       TARGET lives here.
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

            action(LinkedPageAction)
            {
                ApplicationArea = All;
                Caption = 'Linked Page Action';
                RunObject = page "Par Host Page";
                RunPageLink = "No." = field("No.");
            }

            action(ReportRunObjectAction)
            {
                ApplicationArea = All;
                Caption = 'Report Run Object Action';
                RunObject = report "Par Noop Report";
            }

            action(NoEffectAction)
            {
                ApplicationArea = All;
                Caption = 'No Effect Action';
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

            actionref(NoEffectRef; NoEffectAction) { }
        }
    }
}
