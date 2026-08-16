table 64503 "Srmr Lookup Row"
{
    // Issue #1918: declares LookupPageId so Page.RunModal(0, Record) can resolve the
    // page to open from this table's metadata, the way real BC's NavForm.RunModalAsync
    // does (formId==0 && record != null => formId = record.LookupFormId).
    DataClassification = CustomerContent;
    LookupPageId = "Srmr Lookup List";

    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
