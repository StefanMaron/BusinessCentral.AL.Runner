page 65600 "Pbtoos Card"
{
    PageType = Card;
    SourceTable = "Pbtoos Row";
    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
        }
    }

    trigger OnAfterGetCurrRecord()
    var
        Args: Dictionary of [Text, Text];
    begin
        CurrPage.EnqueueBackgroundTask(TaskId, Codeunit::"Pbtoos Worker", Args, 5000);
    end;

    var
        TaskId: Integer;
}
