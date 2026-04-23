/// Minimal stub for the BC system User table (ID 2000000120).
/// The runner pre-seeds this table with the configured user so that
/// AL code calling User.Get(UserSecurityId()) succeeds without errors.
table 2000000120 "User"
{
    DataClassification = EndUserIdentifiableInformation;
    fields
    {
        field(1; "User Security ID"; Guid) { }
        field(2; "User Name"; Code[50]) { }
        field(3; "Full Name"; Text[80]) { }
    }
    keys
    {
        key(PK; "User Security ID") { Clustered = true; }
        key(UserName; "User Name") { }
    }
}
