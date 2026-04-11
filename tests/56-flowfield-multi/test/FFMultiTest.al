codeunit 56562 "FFM Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure MultiFieldExistFalseThenTrue()
    var
        M: Record "FFM Master";
        C: Record "FFM Child";
    begin
        M.Code1 := 'A'; M.Code2 := 'B'; M.Insert();
        M.CalcFields("Exists Flag");
        Assert.IsFalse(M."Exists Flag", 'no matching child yet');

        C.C1 := 'A'; C.C2 := 'B'; C.Insert();
        M.CalcFields("Exists Flag");
        Assert.IsTrue(M."Exists Flag", 'CalcFields should see the matching child');
    end;

    [Test]
    procedure MultiFieldExistNoMatchWithPartialKey()
    var
        M: Record "FFM Master";
        C: Record "FFM Child";
    begin
        M.Code1 := 'X'; M.Code2 := 'Y'; M.Insert();
        C.C1 := 'X'; C.C2 := 'OTHER'; C.Insert();

        M.CalcFields("Exists Flag");
        Assert.IsFalse(M."Exists Flag", 'partial key match must not count as exists');
    end;
}
