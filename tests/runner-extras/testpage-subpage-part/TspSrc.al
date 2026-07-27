table 61960 "TSP Header"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; ReportId; Integer) { }
        field(2; Descr; Text[50]) { }
    }

    keys
    {
        key(PK; ReportId) { Clustered = true; }
    }
}

table 61961 "TSP Line"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; ReportId; Integer) { }
        field(2; LineNo; Integer) { }
        field(3; Name; Text[50]) { }
    }

    keys
    {
        key(PK; ReportId, LineNo) { Clustered = true; }
    }
}

/// <summary>
/// The canonical card-with-lines shape: a part bound to a related table, linked to the
/// current header row by SubPageLink. An AL test reaches the lines only through the part.
/// </summary>
page 61960 "TSP Card"
{
    PageType = Card;
    SourceTable = "TSP Header";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            field(ReportId; Rec.ReportId)
            {
                ApplicationArea = All;
            }
            part(Lines; "TSP Lines")
            {
                ApplicationArea = All;
                SubPageLink = ReportId = field(ReportId);
            }
        }
    }
}

page 61961 "TSP Lines"
{
    PageType = ListPart;
    SourceTable = "TSP Line";
    ApplicationArea = All;

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field(LineNo; Rec.LineNo)
                {
                    ApplicationArea = All;
                }
                field(Name; Rec.Name)
                {
                    ApplicationArea = All;
                }
            }
        }
    }
}

/// <summary>
/// A read-only lines part. Its own page — not the parent card — declares
/// InsertAllowed = false, which is what New() through the part must obey.
/// </summary>
page 61962 "TSP Lines RO"
{
    PageType = ListPart;
    SourceTable = "TSP Line";
    ApplicationArea = All;
    InsertAllowed = false;

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field(LineNo; Rec.LineNo)
                {
                    ApplicationArea = All;
                }
                field(Name; Rec.Name)
                {
                    ApplicationArea = All;
                }
            }
        }
    }
}

/// <summary>
/// Identical to "TSP Card" except for the part it hosts. The card itself stays insertable,
/// so a runner that answered New() from the PARENT page's InsertAllowed would wrongly
/// allow the insert here.
/// </summary>
page 61963 "TSP Card RO Lines"
{
    PageType = Card;
    SourceTable = "TSP Header";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            field(ReportId; Rec.ReportId)
            {
                ApplicationArea = All;
            }
            part(Lines; "TSP Lines RO")
            {
                ApplicationArea = All;
                SubPageLink = ReportId = field(ReportId);
            }
        }
    }
}
