// A setup table that is NEVER given a row, so MissingTestDataDiagnosis's census can measure it
// as genuinely empty. Its only job is to be the table a page trigger fails on.
table 70546 "MTD Setup"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Primary Key"; Code[10]) { DataClassification = SystemMetadata; }
        field(2; Name; Text[30]) { DataClassification = SystemMetadata; }
    }

    keys { key(PK; "Primary Key") { Clustered = true; } }
}
