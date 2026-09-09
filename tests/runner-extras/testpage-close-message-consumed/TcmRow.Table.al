/// <summary>
/// Two jobs, kept in one table so the bundle stays small. The 'OPENED' row is what
/// "Tcm Error Card" writes from OnOpenPage -- present after a refused close, absent if
/// something unwound it. Rows tagged by the [MessageHandler] record that the handler fired
/// and with what text, so no assertion has to run inside the handler itself: an assertion
/// raised there would arrive wrapped in whatever the close path does with an exception, and
/// the test could not tell "the handler never ran" from "the handler ran and disagreed".
/// </summary>
table 65861 "Tcm Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Set ID"; Integer) { }
        field(3; "Last Text"; Text[250]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
