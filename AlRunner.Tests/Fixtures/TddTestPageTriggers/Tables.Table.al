table 72601 "TP Rec"
{
    fields
    {
        field(1; PK; Code[20]) { }
        field(2; Qty; Integer)
        {
            trigger OnValidate()
            var T: Codeunit "TP Target"; R: Integer;
            begin
                R := T.MQtyValidate(1);
            end;
        }
        field(3; Note; Text[50]) { }
        field(4; Extra; Text[50]) { }
    }
    keys { key(PK; PK) { Clustered = true; } }

    trigger OnInsert()
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MRecInsert(1);
    end;

    trigger OnModify()
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MRecModify(1);
    end;

    trigger OnRename()
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MRecRename(1);
    end;

    trigger OnDelete()
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MRecDelete(1);
    end;
}

tableextension 72604 "TP Rec Ext" extends "TP Rec"
{
    fields
    {
        field(72604; ExtNote; Text[50])
        {
            trigger OnValidate()
            var T: Codeunit "TP Target"; R: Integer;
            begin
                R := T.MExtNoteValidate(1);
            end;
        }
    }
}

// A second table, with triggers of its own: a test of the page above must not name them.
table 72610 "TP Quiet"
{
    fields
    {
        field(1; K; Code[20])
        {
            trigger OnValidate()
            var T: Codeunit "TP Target"; R: Integer;
            begin
                R := T.MQuietValidate(1);
            end;
        }
    }
    keys { key(K; K) { Clustered = true; } }

    trigger OnInsert()
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MQuietInsert(1);
    end;
}

// A third, with no trigger and subscribers to its database events (TP Subscribers).
table 72612 "TP Evt"
{
    fields
    {
        field(1; K; Code[20]) { }
        field(2; V; Integer) { }
    }
    keys { key(K; K) { Clustered = true; } }
}
