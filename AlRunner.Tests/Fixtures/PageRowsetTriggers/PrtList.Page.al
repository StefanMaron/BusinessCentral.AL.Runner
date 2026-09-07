// The page under test. It serves every SECOND row of the table out of a page-global temporary
// buffer once the action has run, and forwards Which/Steps to that buffer -- the shape every
// real page of this kind uses. Before the action it takes the ordinary platform path, so one
// page covers both arms of the trigger's own `if`.
page 70643 "PRT List"
{
    PageType = List;
    SourceTable = "PRT Row";
    SourceTableView = sorting("No.") order(descending);
    Editable = false;

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
            action(FillTemp)
            {
                ApplicationArea = All;

                trigger OnAction()
                var
                    Src: Record "PRT Row";
                    Take: Boolean;
                begin
                    TempBuf.Reset();
                    TempBuf.DeleteAll();
                    Src.SetCurrentKey("No.");
                    Src.Ascending(true);
                    Take := true;
                    if Src.FindSet() then
                        repeat
                            if Take then begin
                                TempBuf := Src;
                                TempBuf.Insert();
                            end;
                            Take := not Take;
                        until Src.Next() = 0;
                    RunOnTemp := true;
                    CurrPage.Update(false);
                end;
            }
        }
    }

    var
        TempBuf: Record "PRT Row" temporary;
        Trace: Codeunit "PRT Trace";
        RunOnTemp: Boolean;

    trigger OnFindRecord(Which: Text): Boolean
    var
        Found: Boolean;
    begin
        Trace.Note('F[' + Which + ']');
        if not RunOnTemp then
            exit(Rec.Find(Which));
        TempBuf.Copy(Rec);
        Found := TempBuf.Find(Which);
        if Found then
            Rec := TempBuf;
        exit(Found);
    end;

    trigger OnNextRecord(Steps: Integer): Integer
    var
        Moved: Integer;
    begin
        Trace.Note('N[' + Format(Steps) + ']');
        if not RunOnTemp then
            exit(Rec.Next(Steps));
        TempBuf.Copy(Rec);
        Moved := TempBuf.Next(Steps);
        if Moved <> 0 then
            Rec := TempBuf;
        exit(Moved);
    end;
}
