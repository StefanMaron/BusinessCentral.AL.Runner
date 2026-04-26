// Support table and page for TestPage.Filter.SetFilter object-overload tests.
// Suite 312200 — issue #1459.
table 312200 "TPF Object Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No.";   Code[20])  { }
        field(2; Name;    Text[100]) { }
        field(3; Status;  Code[10])  { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

page 312200 "TPF Object Page"
{
    PageType  = Card;
    SourceTable = "TPF Object Table";

    layout
    {
        area(Content)
        {
            field(NoField;     Rec."No.")  { }
            field(NameField;   Rec.Name)   { }
            field(StatusField; Rec.Status) { }
        }
    }
}

/// <summary>
/// Helper codeunit that routes Variant filter values through the
/// TestPage.Filter.SetFilter() call.  BC emits the Variant parameter as
/// MockVariant (which is NOT NavComplexValue), but the generated call path
/// uses NavIndirectValueToNavValue which returns NavText → string — so the
/// helper exercises the normal, NavText-typed path.
///
/// The purpose of this helper is to exercise the ALSetFilter code path
/// with a dynamically typed argument so the test proves that filter
/// round-trips work correctly via SetFilter → GetFilter.
/// </summary>
codeunit 312201 "TPF Object Helper"
{
    procedure SetFilterViaVariant(var Page: TestPage "TPF Object Page"; FieldFilter: Variant)
    begin
        Page.Filter.SetFilter(Name, FieldFilter);
    end;

    procedure SetNoFilterViaVariant(var Page: TestPage "TPF Object Page"; FieldFilter: Variant)
    begin
        Page.Filter.SetFilter("No.", FieldFilter);
    end;

    procedure SetStatusFilterViaVariant(var Page: TestPage "TPF Object Page"; FieldFilter: Variant)
    begin
        Page.Filter.SetFilter(Status, FieldFilter);
    end;
}
