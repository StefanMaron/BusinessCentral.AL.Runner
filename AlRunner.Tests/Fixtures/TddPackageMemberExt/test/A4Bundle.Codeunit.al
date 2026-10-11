/// <summary>#5491: the other app is a source folder of the run, not a package; its extensions hold 65400.</summary>
codeunit 65403 "Ext Bundle Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure BundleField_TakesAFreeId()
    var
        E: Record "Bundle Entry";
    begin
        E.Bundled := 3;
        if E.FieldNo(Bundled) <> 65401 then
            Error('Expected 65401, got %1', E.FieldNo(Bundled));
    end;

    [Test]
    procedure BundleValue_TakesAFreeOrdinal()
    var
        T: Enum "Bundle Tier";
    begin
        T := "Bundle Tier"::Bundled;
        if T.AsInteger() <> 65401 then
            Error('Expected 65401, got %1', T.AsInteger());
    end;
}
