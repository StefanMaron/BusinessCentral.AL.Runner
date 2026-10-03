/// <summary>
/// #5286: each trigger of this table calls its own missing member directly, so a test reaches that stub
/// only by running the table operation that fires the trigger.
/// </summary>
table 72301 "Trg Rec"
{
    fields
    {
        field(1; PK; Code[20]) { }
        field(2; Qty; Integer)
        {
            trigger OnValidate()
            var
                Target: Codeunit "Trg Target";
                Result: Integer;
            begin
                Result := Target.MissingValidate(1);
            end;
        }
    }
    keys { key(PK; PK) { Clustered = true; } }

    procedure InsertSelf()
    begin
        Insert(true);
    end;

    trigger OnInsert()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingInsert(1);
    end;

    trigger OnModify()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingModify(1);
    end;

    trigger OnDelete()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingDelete(1);
    end;

    trigger OnRename()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingRename(1);
    end;
}
