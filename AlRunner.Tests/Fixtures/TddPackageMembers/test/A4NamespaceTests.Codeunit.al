/// <summary>The table is in the namespace Loyalty.Data and this file does not import it, so the generated tableextension
/// has to name it with its namespace to find it.</summary>
codeunit 65403 "Package Namespace Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NamespacedTable_GetsItsFieldFromAnExtension()
    var
        E: Record Loyalty.Data."Namespaced Entry";
        Read: Record Loyalty.Data."Namespaced Entry";
    begin
        E.Id := 1;
        E.Bonus := 7;
        E.Insert();
        Read.Get(1);
        if Read.Bonus <> 7 then
            Error('Expected 7, got %1', Read.Bonus);
    end;
}
