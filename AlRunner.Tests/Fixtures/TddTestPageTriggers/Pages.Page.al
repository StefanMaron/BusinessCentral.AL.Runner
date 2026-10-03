page 72602 "TP Rec Card"
{
    PageType = Card;
    SourceTable = "TP Rec";
    layout
    {
        area(Content)
        {
            field(PK; Rec.PK) { }
            field(Qty; Rec.Qty) { }
            field(Note; Rec.Note) { }
            // Bound to a variable, not to a field of the table.
            field(Typed; TypedValue) { }
        }
    }
    var
        TypedValue: Text[30];
}

// The controls a page extension adds: a field of the table, and a field of the table extension.
pageextension 72603 "TP Rec Card Ext" extends "TP Rec Card"
{
    layout
    {
        addlast(Content)
        {
            field(Extra; Rec.Extra) { }
            field(ExtNote; Rec.ExtNote) { }
        }
    }
}

page 72611 "TP Quiet Card"
{
    PageType = Card;
    SourceTable = "TP Quiet";
    layout { area(Content) { field(K; Rec.K) { } } }
}

page 72613 "TP Evt Card"
{
    PageType = Card;
    SourceTable = "TP Evt";
    layout { area(Content) { field(K; Rec.K) { } field(V; Rec.V) { } } }
}

// No source table: its control is bound to a variable, so the table a typed value is written to is unknown.
page 72615 "TP Dialog"
{
    PageType = StandardDialog;
    layout { area(Content) { field(Answer; Answer) { } } }
    var
        Answer: Text[30];
}

page 72618 "TP Lines Part"
{
    PageType = ListPart;
    SourceTable = "TP Quiet";
    layout { area(Content) { repeater(Rows) { field(K; Rec.K) { } } } }
}

page 72617 "TP Parent Card"
{
    PageType = Card;
    SourceTable = "TP Parent";
    layout { area(Content) { field(K; Rec.K) { } part(Lines; "TP Lines Part") { } } }
}
