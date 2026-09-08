/// <summary>
/// One row per data-item iteration, so an iteration COUNT is directly observable from AL.
/// A duration would be flaky and would pass on a fast box with the defect present, which is
/// why the assertions in "RSM Tests" read this table instead.
/// </summary>
table 65840 "RSM Counter"
{
    DataClassification = ToBeClassified;
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Loop Name"; Code[20]) { }
    }
    keys { key(PK; "Entry No.") { Clustered = true; } }
}
