table 71900 "PIT Row"
{
    fields { field(1; "No."; Code[20]) { } }
    keys { key(PK; "No.") { Clustered = true; } }
}

page 71901 "PIT Wizard"
{
    PageType = NavigatePage;
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            field(TraceText; Trace) { ApplicationArea = All; Editable = false; }
        }
    }
    actions
    {
        area(Processing)
        {
            action(NextAction)
            {
                ApplicationArea = All;
                Enabled = NextEnabled;
                InFooterBar = true;
                trigger OnAction()
                var
                    Row: Record "PIT Row";
                begin
                    Row."No." := 'NEXT';
                    Row.Insert();
                end;
            }
            action(BackAction)
            {
                ApplicationArea = All;
                Enabled = BackEnabled;
                InFooterBar = true;
                trigger OnAction()
                var
                    Row: Record "PIT Row";
                begin
                    Row."No." := 'BACK';
                    Row.Insert();
                end;
            }
        }
    }

    trigger OnInit()
    begin
        Trace += 'I';
        NextEnabled := true;
        BackEnabled := false;
    end;

    trigger OnOpenPage()
    begin
        Trace += 'O';
    end;

    var
        Trace: Text[10];
        NextEnabled: Boolean;
        BackEnabled: Boolean;
}

page 71902 "PIT Failing Init"
{
    PageType = Card;
    ApplicationArea = All;
    UsageCategory = Administration;

    trigger OnInit()
    begin
        Error('PIT OnInit refused');
    end;
}

codeunit 71903 "PIT Tests"
{
    Subtype = Test;

    var
        SeenTrace: Text;
        SeenNextEnabled: Boolean;

    [Test]
    procedure OpenEdit_RunsOnInitOnceBeforeOnOpenPage()
    var
        Wiz: TestPage "PIT Wizard";
    begin
        Wiz.OpenEdit();
        if Wiz.TraceText.Value() <> 'IO' then Error('Trace=%1', Wiz.TraceText.Value());
    end;

    [Test]
    procedure ActionBoundToOnInitGlobals_EnabledAndInvokeFollowThem()
    var
        Wiz: TestPage "PIT Wizard";
        Row: Record "PIT Row";
    begin
        Row.DeleteAll();
        Wiz.OpenEdit();
        if not Wiz.NextAction.Enabled() then Error('Next must be enabled');
        if Wiz.BackAction.Enabled() then Error('Back must be disabled');
        Wiz.NextAction.Invoke();
        Wiz.BackAction.Invoke();
        if not Row.Get('NEXT') then Error('Next OnAction did not run');
        if Row.Get('BACK') then Error('Back OnAction ran');
    end;

    [Test]
    procedure Reopen_RunsOnInitAgain()
    var
        Wiz: TestPage "PIT Wizard";
    begin
        Wiz.OpenEdit();
        Wiz.Close();
        Wiz.OpenEdit();
        if Wiz.TraceText.Value() <> 'IO' then Error('reopen Trace=%1', Wiz.TraceText.Value());
    end;

    [Test]
    [HandlerFunctions('WizModalHandler')]
    procedure RunModal_RunsOnInitOnce()
    var
        Wiz: Page "PIT Wizard";
    begin
        SeenTrace := '?';
        Wiz.RunModal();
        if SeenTrace <> 'IO' then Error('modal Trace=%1', SeenTrace);
        if not SeenNextEnabled then Error('modal Next must be enabled');
    end;

    [Test]
    [HandlerFunctions('WizPageHandler')]
    procedure PageRun_RunsOnInitOnce()
    begin
        SeenTrace := '?';
        Page.Run(Page::"PIT Wizard");
        if SeenTrace <> 'IO' then Error('run Trace=%1', SeenTrace);
    end;

    [Test]
    procedure ErrorInOnInit_ReachesTheTest()
    var
        Failing: TestPage "PIT Failing Init";
    begin
        asserterror Failing.OpenView();
        if GetLastErrorText() <> 'PIT OnInit refused' then Error('last error=%1', GetLastErrorText());
    end;

    [ModalPageHandler]
    procedure WizModalHandler(var Wiz: TestPage "PIT Wizard")
    begin
        SeenTrace := Wiz.TraceText.Value();
        SeenNextEnabled := Wiz.NextAction.Enabled();
    end;

    [PageHandler]
    procedure WizPageHandler(var Wiz: TestPage "PIT Wizard")
    begin
        SeenTrace := Wiz.TraceText.Value();
    end;
}
