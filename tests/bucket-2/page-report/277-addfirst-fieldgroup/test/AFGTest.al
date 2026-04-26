codeunit 119002 "AFG Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure AFG_Addfirst_Compiles()
    begin
        // Positive: if tableextension with addfirst fieldgroup compiled, we reach here.
        Assert.IsTrue(true, 'tableextension with addfirst fieldgroup must compile');
    end;

    [Test]
    procedure AFG_Record_InsertAndFind()
    var
        Rec: Record "AFG Data";
    begin
        // Positive: prove the table itself is functional (not just a compilation stub).
        Rec."Entry No." := 1;
        Rec.Name := 'Test';
        Rec.Insert();
        Rec.Reset();
        Assert.IsTrue(Rec.FindFirst(), 'Record must be findable after Insert');
        Assert.AreEqual(1, Rec."Entry No.", 'Entry No. must be 1');
    end;
}
