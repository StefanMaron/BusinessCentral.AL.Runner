// One object of each kind BC's emitter produces a metadata document for.
//
// Table, page, codeunit, enum, report and xmlport all deliberately carry the SAME
// numeric id (70660). AL allows that — ids are unique per object type — and it is the
// point of the fixture: a metadata registry keyed on the id alone collapses six
// documents into one, and a per-kind assertion is the only thing that notices.

table 70660 "OMR Thing"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
        field(2; Description; Text[50]) { DataClassification = CustomerContent; Editable = false; }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
        key(ByDescription; Description) { }
    }
}

page 70660 "OMR Thing List"
{
    PageType = List;
    SourceTable = "OMR Thing";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                field(Description; Rec.Description) { ApplicationArea = All; }
            }
        }
    }
}

enum 70660 "OMR Kind"
{
    Extensible = true;

    value(0; Plain) { Caption = 'Plain kind'; }
    value(1; Fancy) { Caption = 'Fancy kind'; }
}

interface "OMR IThing"
{
    procedure Describe(): Text;
}

report 70660 "OMR Report"
{
    ProcessingOnly = true;
    UseRequestPage = false;

    dataset
    {
        dataitem(Thing; "OMR Thing")
        {
            column(EntryNo; "Entry No.") { }
        }
    }
}

xmlport 70660 "OMR Port"
{
    Format = Xml;
    Direction = Export;

    schema
    {
        textelement(Root)
        {
            tableelement(Thing; "OMR Thing")
            {
                fieldelement(EntryNo; Thing."Entry No.") { }
            }
        }
    }
}

permissionset 70661 "OMR PS"
{
    Assignable = true;
    Caption = 'OMR base permissions';
    Permissions = tabledata "OMR Thing" = RIMD;
}

tableextension 70662 "OMR Thing Ext" extends "OMR Thing"
{
    fields
    {
        field(50; "Extra Note"; Text[30]) { DataClassification = CustomerContent; }
    }
}

pageextension 70663 "OMR Thing List Ext" extends "OMR Thing List"
{
    layout
    {
        addlast(Content)
        {
            field("Extra Note"; Rec."Extra Note") { ApplicationArea = All; }
        }
    }
}

enumextension 70664 "OMR Kind Ext" extends "OMR Kind"
{
    value(2; Extended) { Caption = 'Extended kind'; }
}

reportextension 70666 "OMR Report Ext" extends "OMR Report"
{
    // The thirteenth kind. It was missing here, and its absence made
    // docs/object-metadata-capture.md report twelve kinds arriving when thirteen do --
    // the doc was describing this fixture rather than the compiler. Base Application
    // emits 14 ReportExtension documents, so the kind was always arriving; nothing here
    // could see it.
    dataset
    {
        add(Thing)
        {
            column(DescriptionExt; Description) { }
        }
    }
}

permissionsetextension 70665 "OMR PS Ext" extends "OMR PS"
{
    Permissions = tabledata "OMR Thing" = RIMD;
}

// A query carries a metadata document too, and the issue's own table of twelve kinds
// does not list it — which is the point of capturing on "the metadata string is
// non-empty" rather than on a list of kinds someone remembered to write down.
query 70660 "OMR Query"
{
    QueryType = Normal;

    elements
    {
        dataitem(Thing; "OMR Thing")
        {
            column(EntryNo; "Entry No.") { }
        }
    }
}
