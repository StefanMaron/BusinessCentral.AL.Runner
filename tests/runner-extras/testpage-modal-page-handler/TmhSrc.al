table 61920 "TMH Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Descr; Text[50]) { }
        field(3; Picked; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// <summary>The modal page under test — what a [ModalPageHandler] is handed.</summary>
page 61920 "TMH Modal"
{
    PageType = Card;
    SourceTable = "TMH Row";
    ApplicationArea = All;

    layout
    {
        area(Content)
        {
            field(Descr; Rec.Descr)
            {
                ApplicationArea = All;
            }
        }
    }
}

/// <summary>
/// Hosts the action that opens the modal page. The OnAction records what RunModal
/// returned, so a test can tell "the handler ran" from "the handler's answer got back".
/// </summary>
page 61921 "TMH Host"
{
    PageType = List;
    SourceTable = "TMH Row";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Descr; Rec.Descr) { ApplicationArea = All; }

                // A LOOKUP-mode modal, which closes with LookupOK rather than OK. The
                // `<> Action::LookupOK` gate is the documented AL idiom for a lookup and
                // is what makes this field a regression test rather than a duplicate of
                // the action-driven RunModal above.
                field(Picked; Rec.Picked)
                {
                    ApplicationArea = All;
                    Lookup = true;

                    trigger OnLookup(var Text: Text): Boolean
                    var
                        Modal: Page "TMH Modal";
                    begin
                        Modal.LookupMode(true);
                        if Modal.RunModal() <> Action::LookupOK then
                            exit(false);
                        Text := 'PICKED';
                        exit(true);
                    end;
                }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(PickIt)
            {
                ApplicationArea = All;
                Caption = 'Pick It';

                trigger OnAction()
                var
                    Modal: Page "TMH Modal";
                    Outcome: Record "TMH Row";
                    Result: Action;
                begin
                    Result := Modal.RunModal();

                    Outcome.Init();
                    Outcome."No." := 'RESULT';
                    Outcome.Descr := Format(Result);
                    if not Outcome.Insert() then
                        Outcome.Modify();
                end;
            }

            action(PickWithVars)
            {
                ApplicationArea = All;
                Caption = 'Pick With Vars';

                trigger OnAction()
                var
                    Modal: Page "TMH Modal Vars";
                begin
                    Modal.RunModal();
                end;
            }
        }
    }
}

/// <summary>
/// A modal page whose selector binds to a PAGE VARIABLE rather than to a Rec field — the
/// shape of every picker that puts a mode selector above its list, and what Pageworks'
/// InsertPicker does.
///
/// It only matters here because this page is opened by AL with RunModal: the runner never
/// constructs that instance, so it is not the one the TestPage machinery built and marked.
/// A Rec-bound control on the same page resolves either way, which is why the two are
/// tested side by side.
/// </summary>
page 61922 "TMH Modal Vars"
{
    PageType = List;
    SourceTable = "TMH Row";
    ApplicationArea = All;

    layout
    {
        area(Content)
        {
            field(Mode; SelectedMode)
            {
                ApplicationArea = All;
                Caption = 'Mode';

                trigger OnValidate()
                var
                    Echo: Record "TMH Row";
                begin
                    // Writing through to the table is what proves the page's own AL ran,
                    // rather than a value having been stashed and handed back.
                    Echo.Init();
                    Echo."No." := 'MODE';
                    Echo.Descr := CopyStr(SelectedMode, 1, MaxStrLen(Echo.Descr));
                    if not Echo.Insert() then
                        Echo.Modify();
                end;
            }
            repeater(Rows)
            {
                field(Descr; Rec.Descr) { ApplicationArea = All; }
            }
        }
    }

    var
        SelectedMode: Text;
}
