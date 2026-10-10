codeunit 65220 "Probe Twin Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure FieldGoesToTheTable()
    var
        Row: Record "Twin";
    begin
        Row."Code" := 1;
        Row."Weight" := 2;
        Row.Insert();
        Clear(Row);
        Row.Get(1);
        if Row."Weight" <> 2 then
            Error('weight %1', Row."Weight");
    end;
}
