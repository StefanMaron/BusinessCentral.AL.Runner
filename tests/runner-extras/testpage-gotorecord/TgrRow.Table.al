/// <summary>
/// Backing table for the TestPage under test. Deliberately trivial: a Code[20]
/// primary key plus one text field, so GoToRecord's primary-key lookup has an
/// unambiguous single-field key to match on.
/// </summary>
table 61810 "TGR Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = CustomerContent;
        }
        field(2; Descr; Text[50])
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
