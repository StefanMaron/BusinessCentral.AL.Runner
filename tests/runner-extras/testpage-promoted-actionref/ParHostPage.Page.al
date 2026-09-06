/// The page under test. Every action that carries a trigger lives in area(Processing); every
/// actionref lives in area(Promoted) and delegates to one of them.
///
/// The last four actions are the negative controls, and they are what an implementation that
/// resolved "no trigger found" to "run nothing, quietly" would fail:
///
///   TriggerlessAction / TriggerlessRef  a RunObject naming a PAGE. Since #2931 the runner
///                                       PERFORMS this rather than refusing it, and since #2975
///                                       it performs it even with no [PageHandler] bound -- the
///                                       target opens unattended and nothing is raised. The
///                                       target page records its own opening so the arms have
///                                       something to assert.
///   LinkedPageAction                    the same, plus RunPageLink. Since #2942 the runner
///                                       applies the link and opens the target on the rowset it
///                                       selects; since #2975 it does so with no [PageHandler]
///                                       bound too, so this arm asserts the target opened AND
///                                       that it opened FILTERED, rather than an error. What the
///                                       link SELECTS is pinned upstream in the al-language
///                                       corpus (handlers/TestPageActionRunPageLink.al).
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
                // Not the host page: see ParRunObjectTarget.Page.al. The target records its own
                // opening, which is what makes the two RunObject arms falsifiable now that
                // (#2975) an unattended RunObject raises nothing to assert on.
                RunObject = page "Par RunObject Target";
            }

            action(LinkedPageAction)
            {
                ApplicationArea = All;
                Caption = 'Linked Page Action';
                // Same target as TriggerlessAction, and for the same reason (#2975): with no
                // [PageHandler] bound nothing is raised any more, so the only way to tell a
                // performed RunObject from a silently skipped one is a target that records its
                // own opening. This one also records whether it opened FILTERED, which is what
                // separates this arm from the unlinked one.
                RunObject = page "Par RunObject Target";
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
