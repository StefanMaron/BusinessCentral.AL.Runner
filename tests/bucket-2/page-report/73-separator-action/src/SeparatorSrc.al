/// Helper codeunit in the same compilation unit as a page with separator() actions.
codeunit 73002 "Sep Helper"
{
    procedure GetValue(): Text
    begin
        exit('ok');
    end;

    procedure Multiply(a: Integer; b: Integer): Integer
    begin
        exit(a * b);
    end;
}

/// Page with separator() elements between actions.
/// separator() is a pure UI layout hint — it draws a horizontal divider in menus/toolbars.
/// The runner must compile this without errors even though there is no UI.
page 73002 "Sep Test Page"
{
    PageType = Card;

    actions
    {
        area(Processing)
        {
            action(Action1)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                end;
            }
            separator(Sep1)
            {
            }
            action(Action2)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                end;
            }
        }
        area(Navigation)
        {
            action(NavAction)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                end;
            }
            separator(Sep2)
            {
            }
        }
    }
}
