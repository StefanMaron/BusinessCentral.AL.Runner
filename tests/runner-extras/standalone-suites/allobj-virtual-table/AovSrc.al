/// <summary>
/// Backing table for the probe report — and itself one of the objects AllObj
/// must report as existing.
/// </summary>
table 61860 "AOV Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
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

/// <summary>
/// A report in this app's own ID range. Pageworks gates on exactly this —
/// AllObj.Get(AllObj."Object Type"::Report, ReportId) — before rendering, and
/// takes its 'reportNotFound' branch when the lookup lies.
/// </summary>
report 61860 "AOV Probe Report"
{
    Caption = 'AOV Probe Report';
    UsageCategory = None;

    dataset
    {
        dataitem(Row; "AOV Row")
        {
            column(No_; Row."No.")
            {
            }
        }
    }
}

/// <summary>
/// A codeunit in range, so the test can prove the provider distinguishes object
/// TYPES rather than answering true for any id it has ever seen.
/// </summary>
codeunit 61861 "AOV Probe Codeunit"
{
    procedure Ping(): Integer
    begin
        exit(61861);
    end;
}
