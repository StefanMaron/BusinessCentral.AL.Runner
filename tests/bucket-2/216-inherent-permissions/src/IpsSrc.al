/// Source codeunit with Permissions object property (issue #978).
/// The BC compiler generates a 'protected override NavPermissionList InherentPermissionsList'
/// member in codeunit classes (alongside the existing IndirectPermissionList) when the
/// Permissions property is present in newer BC versions.  The RoslynRewriter must strip
/// that member because AlScope does not expose a virtual InherentPermissionsList to
/// override (CS0115 otherwise).
codeunit 97905 "IPS Src"
{
    Permissions = tabledata "IPS Table" = R;

    procedure Echo(s: Text): Text
    begin
        exit(s);
    end;

    procedure Add(a: Integer; b: Integer): Integer
    begin
        exit(a + b);
    end;
}

/// Minimal table for Permissions reference.
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
