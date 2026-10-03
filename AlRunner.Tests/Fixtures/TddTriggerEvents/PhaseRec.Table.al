/// <summary>
/// #5286: a table extension names its triggers OnBefore<Operation> and OnAfter<Operation>, and a modify() block
/// of one OnBeforeValidate and OnAfterValidate; the operation that starts the base table's trigger starts these
/// too. Each calls its own missing member. The OnLookup of the field it adds is a page's, never an operation's.
/// </summary>
table 72308 "Trg Phase Rec"
{
    fields
    {
        field(1; PK; Code[20]) { }
        field(2; Qty; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

tableextension 72352 "Trg Phase Ext" extends "Trg Phase Rec"
{
    fields
    {
        field(72340; Lk; Code[10])
        {
            trigger OnLookup()
            var
                Target: Codeunit "Trg Target";
                Result: Integer;
            begin
                Result := Target.MissingExtLookup(1);
            end;
        }
        modify(Qty)
        {
            trigger OnBeforeValidate()
            var
                Target: Codeunit "Trg Target";
                Result: Integer;
            begin
                Result := Target.MissingExtBeforeValidate(1);
            end;

            trigger OnAfterValidate()
            var
                Target: Codeunit "Trg Target";
                Result: Integer;
            begin
                Result := Target.MissingExtAfterValidate(1);
            end;
        }
    }

    trigger OnBeforeInsert()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingExtBeforeInsert(1);
    end;

    trigger OnAfterInsert()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingExtAfterInsert(1);
    end;

    trigger OnBeforeModify()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingExtBeforeModify(1);
    end;

    trigger OnAfterModify()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingExtAfterModify(1);
    end;

    trigger OnBeforeDelete()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingExtBeforeDelete(1);
    end;

    trigger OnAfterDelete()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingExtAfterDelete(1);
    end;

    trigger OnBeforeRename()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingExtBeforeRename(1);
    end;

    trigger OnAfterRename()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingExtAfterRename(1);
    end;
}
