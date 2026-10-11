/// <summary>The file above was dropped, so nothing declares "Ghost": the table has the one field the package gives it.</summary>
codeunit 65411 "Package Ghost Reader Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure GhostField_IsDeclaredByNobody()
    var
        R: RecordRef;
    begin
        R.Open(65387);
        if R.FieldCount() <> 1 then
            Error('Expected 1 field, got %1', R.FieldCount());
    end;
}
