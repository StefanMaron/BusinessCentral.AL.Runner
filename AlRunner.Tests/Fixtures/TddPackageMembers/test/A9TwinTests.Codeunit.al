/// <summary>Two package tables are called "Namespaced Entry", in Loyalty.Data and in Other.Data. "Bonus" is assigned on this one
/// (the twin, which has a "Twin Points" field the first lacks) and on the first one in A4NamespaceTests: an extension that
/// named its table without the namespace would land on the first for both.</summary>
codeunit 65408 "Package Twin Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure TwinTable_GetsItsOwnExtension()
    var
        E: Record Other.Data."Namespaced Entry";
        Read: Record Other.Data."Namespaced Entry";
    begin
        E.Id := 5;
        E."Twin Points" := 4;
        E.Bonus := 9;
        E.Insert();
        Read.Get(5);
        if (Read."Twin Points" <> 4) or (Read.Bonus <> 9) then
            Error('Expected 4 and 9, got %1 and %2', Read."Twin Points", Read.Bonus);
    end;
}
