/// <summary>#5301: where the library's members are generated, into the library's own compile.</summary>
codeunit 72700 "TPL Lib Target"
{
    procedure Placeholder()
    begin
    end;
}

table 72701 "TPL Rec"
{
    fields
    {
        field(1; PK; Code[20]) { }
        field(2; Qty; Integer)
        {
            trigger OnValidate()
            var T: Codeunit "TPL Lib Target"; R: Integer;
            begin
                R := T.MLibValidate(1);
            end;
        }
    }
    keys { key(PK; PK) { Clustered = true; } }

    trigger OnInsert()
    var T: Codeunit "TPL Lib Target"; R: Integer;
    begin
        R := T.MLibInsert(1);
    end;
}

page 72702 "TPL Card"
{
    PageType = Card;
    SourceTable = "TPL Rec";
    layout { area(Content) { field(PK; Rec.PK) { } field(Qty; Rec.Qty) { } } }
}

/// <summary>A procedure of the library that opens a new record on the page: the test bundle calls it.</summary>
codeunit 72703 "TPL Helper"
{
    procedure CreateThroughThePage()
    var P: TestPage "TPL Card";
    begin
        P.OpenNew();
        P.Close();
    end;
}

/// <summary>A field the library adds to its own table, with a trigger, and a page control for it: the control
/// reaches the test bundle as a symbol of a dependency.</summary>
tableextension 72704 "TPL Rec Ext" extends "TPL Rec"
{
    fields
    {
        field(72704; ExtQty; Integer)
        {
            trigger OnValidate()
            var T: Codeunit "TPL Lib Target"; R: Integer;
            begin
                R := T.MLibExtValidate(1);
            end;
        }
    }
}

pageextension 72705 "TPL Card Ext" extends "TPL Card"
{
    layout { addlast(Content) { field(ExtQty; Rec.ExtQty) { } } }
}

/// <summary>A table the TestPages below never touch: its trigger must not be named.</summary>
table 72706 "TPL Other"
{
    fields { field(1; K; Code[20]) { } }
    keys { key(K; K) { Clustered = true; } }

    trigger OnInsert()
    var T: Codeunit "TPL Lib Target"; R: Integer;
    begin
        R := T.MLibOtherInsert(1);
    end;
}
