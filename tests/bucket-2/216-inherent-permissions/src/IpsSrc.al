/// Source codeunit with InherentPermissions object property (issue #978).
/// The BC compiler generates a 'protected override NavPermissionList InherentPermissionsList'
/// member in the codeunit class and its inner scope classes when the AL
/// InherentPermissions object property is set.  The RoslynRewriter must strip
/// that member (like IndirectPermissionList) because AlScope does not expose a
/// virtual InherentPermissionsList to override.
codeunit 97905 "IPS Src"
{
    InherentPermissions = TableData "IPS Table" = R;

    procedure Echo(s: Text): Text
    begin
        exit(s);
    end;

    procedure Add(a: Integer; b: Integer): Integer
    begin
        exit(a + b);
    end;
}

/// Minimal table for InherentPermissions reference.
table 97904 "IPS Table"
{
    fields
    {
        field(1; Id; Integer) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
