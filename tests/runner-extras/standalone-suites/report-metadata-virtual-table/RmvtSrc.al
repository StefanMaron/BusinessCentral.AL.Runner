/// <summary>
/// Root data-item table of the probe report — the table
/// <c>Report Metadata.FirstDataItemTableID</c> must report for report 61950.
/// </summary>
table 61950 "RMVT Header"
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
/// Nested data-item table. Present so the Report Data Items rows can be proven
/// to carry a real indentation level and a per-data-item related table, rather
/// than one flat row echoing the report's first table.
/// </summary>
table 61951 "RMVT Line"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Header No."; Code[20])
        {
            DataClassification = CustomerContent;
        }
        field(2; "Line No."; Integer)
        {
            DataClassification = CustomerContent;
        }
    }

    keys
    {
        key(PK; "Header No.", "Line No.")
        {
            Clustered = true;
        }
    }
}

/// <summary>
/// A dataset report with a two-level data-item tree and a Caption that differs
/// from its object name — so a provider that filled Caption from Name would
/// fail the Caption assertion.
/// </summary>
report 61950 "RMVT Doc Report"
{
    Caption = 'RMVT Document Report';
    UsageCategory = None;

    dataset
    {
        dataitem(Header; "RMVT Header")
        {
            column(No_; Header."No.")
            {
            }

            dataitem(Line; "RMVT Line")
            {
                DataItemLink = "Header No." = field("No.");

                column(LineNo_; Line."Line No.")
                {
                }
            }
        }
    }
}

/// <summary>
/// A processing-only report with no dataset at all — the exact shape Pageworks'
/// discovery entity set filters OUT via <c>FirstDataItemTableID &lt;&gt; 0</c>.
/// </summary>
report 61951 "RMVT ProcessingOnly Report"
{
    Caption = 'RMVT Processing Only Report';
    ProcessingOnly = true;
    UsageCategory = None;

    trigger OnPostReport()
    begin
    end;
}
