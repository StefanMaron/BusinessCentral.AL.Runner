codeunit 56552 "FF Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CalcFieldsExistsFalseWhenNoChild()
    var
        M: Record "FF Master";
    begin
        M.Id := 1;
        M.Insert();

        M.CalcFields("Has Child");
        Assert.IsFalse(M."Has Child", 'No children inserted — exist FlowField must be false');
    end;

    [Test]
    procedure CalcFieldsExistsTrueAfterChildInsert()
    var
        M: Record "FF Master";
        C: Record "FF Child";
    begin
        M.Id := 2;
        M.Insert();

        C.Id := 1;
        C.ParentId := 2;
        C.Insert();

        M.CalcFields("Has Child");
        Assert.IsTrue(M."Has Child", 'Child with ParentId=2 exists — exist FlowField must be true');
    end;
}
