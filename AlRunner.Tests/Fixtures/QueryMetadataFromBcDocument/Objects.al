table 70720 "QMD Head"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Amount; Decimal) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

table 70721 "QMD Line"
{
    fields
    {
        field(1; "Head No."; Code[20]) { }
        field(2; "Line No."; Integer) { }
        field(3; Qty; Decimal) { }
    }
    keys { key(PK; "Head No.", "Line No.") { Clustered = true; } }
}

// Declares a DIFFERENT value for every constant the runner used to hardcode, so a run that
// still hardcodes them cannot produce this query's trace line. The nested dataitem omits
// SqlJoinType, which is the one with AL-observable consequences.
query 70720 "QMD Divergent"
{
    QueryType = API;
    APIPublisher = 'alrunner';
    APIGroup = 'qmd';
    APIVersion = 'v1.0';
    EntityName = 'qmdDivergent';
    EntitySetName = 'qmdDivergents';
    ReadState = ReadShared;
    TopNumberOfRows = 5;
    QueryCategory = 'Lists';

    elements
    {
        dataitem(Head; "QMD Head")
        {
            column(HeadNo; "No.") { }
            column(TotalAmount; Amount)
            {
                Method = Sum;
            }

            dataitem(Line; "QMD Line")
            {
                DataItemLink = "Head No." = Head."No.";

                column(LineQty; Qty) { }
            }
        }
    }
}

// Declares NONE of them: the control. Its trace line must carry the defaults, so no single
// constant can satisfy this query and 70720 at once.
query 70721 "QMD Plain"
{
    QueryType = Normal;

    elements
    {
        dataitem(H; "QMD Head")
        {
            filter(HFilterNo; "No.") { }
            column(HAmount; Amount) { }
        }
    }
}
