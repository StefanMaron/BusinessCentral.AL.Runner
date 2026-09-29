// Three fields, one per case RunnerPageInstance.RaiseOnLookup has to tell apart. The two
// trigger-bearing fields write a value naming WHERE the trigger ran, so a failure reports
// which handler fired and not only that the wrong one did.
table 65561 "Tlr Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = CustomerContent;
        }
        // The subject. No trigger on either side, so the lookup comes from the TableRelation,
        // whose target (this same table, to keep the bundle to one table) declares no
        // LookupPageId or DrillDownPageId.
        field(2; "Relation Only"; Code[20])
        {
            DataClassification = CustomerContent;
            TableRelation = "Tlr Row"."No.";
        }
        // Scoping control: the table field carries the trigger and the page control does not.
        field(3; "Table Trigger"; Code[20])
        {
            DataClassification = CustomerContent;

            trigger OnLookup()
            begin
                "Table Trigger" := 'FROM-TABLE';
            end;
        }
        // Scoping control: the page control carries the trigger (see Tlr Card).
        field(4; "Control Trigger"; Code[20])
        {
            DataClassification = CustomerContent;
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
    }
}
