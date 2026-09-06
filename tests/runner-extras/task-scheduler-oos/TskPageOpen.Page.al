// Issue #3212 — a refusal raised from a page's OnOpenPage must reach the AL caller.
//
// The runner opens a TestPage by driving the page's lifecycle triggers itself, from
// RunnerTestPageState.MarkOpened, which runs inside BC's Cecil-rewritten NavTestPage.Open
// and therefore carries a catch-all so a runner-internal reflection failure cannot tear
// through BC's own IL. That catch-all was filtered on `ex is not NavBaseException` — BC's
// AL-error hierarchy — which let a genuine AL error out (#2677) but still swallowed
// RunnerOutOfScopeException, deliberately a plain System.Exception so that no BC error path
// can produce it.
//
// So EVERY out-of-scope refusal raised from an OnOpenPage was discarded silently: the page
// opened as though the trigger had succeeded, and the test failed later on whatever the
// trigger had not done. Found while fixing #3212, where Base Application page 2158
// "O365 Brand Colors" reaches System.Drawing from OnOpenPage: ten of the eleven failing
// tests named the surface once the interop refusal existed, and the eleventh — the only one
// going through a page — still reported "Expected number of O365 Brand Color entries: 12.
// Actual: 0", with no mention of what had actually stopped it.
//
// The task-scheduler surface stands in for System.Drawing here because it refuses without
// needing anything from the Base Application, and because this suite already owns it.

table 65604 "Tsk Page Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
        field(2; Marker; Text[30]) { DataClassification = CustomerContent; }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

// OnOpenPage touches an out-of-scope surface. Opening this page must fail with the refusal.
page 65605 "Tsk Refusing Page"
{
    PageType = List;
    SourceTable = "Tsk Page Row";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field(Marker; Rec.Marker) { ApplicationArea = All; }
            }
        }
    }

    trigger OnOpenPage()
    var
        Ignored: Boolean;
    begin
        Ignored := TaskScheduler.TaskExists(CreateGuid());
    end;
}

// Scoping control for the above: an OnOpenPage that refuses nothing must still run, and the
// page must still open. Widening the catch-all is only correct if it changes exactly the one
// case; a fix that let unrelated failures escape would break this page instead.
page 65606 "Tsk Quiet Page"
{
    PageType = List;
    SourceTable = "Tsk Page Row";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field(Marker; Rec.Marker) { ApplicationArea = All; }
            }
        }
    }

    trigger OnOpenPage()
    var
        Row: Record "Tsk Page Row";
    begin
        Row."Entry No." := 1;
        Row.Marker := 'opened';
        Row.Insert();
    end;
}
