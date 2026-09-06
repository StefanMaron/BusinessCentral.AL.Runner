// Issue #3044 — the source table for Date_FlowFieldOverDate_StillDemandsTheWholeDocumentedWindow.
//
// A FlowField whose CalcFormula source is the Date virtual table reaches
// TempTableDataProvider without a DataCacheRequest, so none of the DataAccess-level Date
// window guards ever sees it. It is the exact shape #2988's provider-level net was built for,
// and the control proving #3044 did not weaken that net: this FlowField names no closed
// "Period Start" bound, so it must still demand the whole 1900..2099 window and refuse under
// AL_RUNNER_DATE_WINDOW_MAX_ROWS=2000.
table 61632 "DWL Date Count Holder"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[10]) { DataClassification = CustomerContent; }
        field(2; "Date Rows"; Integer)
        {
            FieldClass = FlowField;
            CalcFormula = count(Date);
            Editable = false;
        }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
