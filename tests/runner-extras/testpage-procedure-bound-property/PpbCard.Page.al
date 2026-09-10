/// Every property here that names IsAllowed() compiles with
///     warning AL0573: Procedure calls is not valid for client expressions.
/// That is the point of the page: real BC does not evaluate such an expression at all, so an
/// action declaring `Enabled = IsAllowed()` reads false and its OnAction never runs, even though
/// the procedure returns true unconditionally (measured on BC 28.4.53241.0, issue #3731).
page 65913 "Ppb Card"
{
    PageType = Card;
    SourceTable = "Ppb Row";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            group(General)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(FlagProcEnabled; Rec.Flag)
                {
                    ApplicationArea = All;
                }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(AProcEnabled)
            {
                ApplicationArea = All;
                Enabled = IsAllowed();

                trigger OnAction()
                var
                    Trace: Record "Ppb Trace";
                begin
                    Trace.Log('PROC-ENABLED');
                end;
            }
            action(AProcVisible)
            {
                ApplicationArea = All;
                // Unmeasured on real BC, same as the control above.
                Visible = IsAllowed();

                trigger OnAction()
                var
                    Trace: Record "Ppb Trace";
                begin
                    Trace.Log('PROC-VISIBLE');
                end;
            }
            action(ANoProp)
            {
                ApplicationArea = All;

                trigger OnAction()
                var
                    Trace: Record "Ppb Trace";
                begin
                    Trace.Log('NO-PROP');
                end;
            }
            action(ARecFlag)
            {
                ApplicationArea = All;
                Enabled = Rec.Flag;

                trigger OnAction()
                var
                    Trace: Record "Ppb Trace";
                begin
                    Trace.Log('REC-FLAG');
                end;
            }
            action(ALiteralTrue)
            {
                ApplicationArea = All;
                Enabled = true;

                trigger OnAction()
                var
                    Trace: Record "Ppb Trace";
                begin
                    Trace.Log('LITERAL-TRUE');
                end;
            }
        }
    }

    procedure IsAllowed(): Boolean
    begin
        exit(true);
    end;
}
