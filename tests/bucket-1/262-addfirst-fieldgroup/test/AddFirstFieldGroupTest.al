codeunit 61813 "AFG Field Group Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: tableextension with addfirst fieldgroup compiles and runs.
    // ------------------------------------------------------------------

    [Test]
    procedure FieldGroup_TableExtCompiles()
    var
        Rec: Record "AFG Base Table";
        Src: Codeunit "AFG Field Group Src";
    begin
        // [GIVEN] A record with Id set to 42
        Rec.Id := 42;
        // [WHEN]  We read Id through the helper (which is in the same tableextension)
        // [THEN]  The value is exactly 42 — proving the tableextension compiled
        Assert.AreEqual(42, Src.GetId(Rec), 'Id must be 42');
    end;

    [Test]
    procedure FieldGroup_NameRoundtrips()
    var
        Rec: Record "AFG Base Table";
        Src: Codeunit "AFG Field Group Src";
    begin
        // [GIVEN] A record with Name set
        Rec.Name := 'Widget';
        // [WHEN]  We read Name through the helper
        // [THEN]  The exact string is returned — proving no data corruption
        Assert.AreEqual('Widget', Src.GetName(Rec), 'Name must be Widget');
    end;

    // ------------------------------------------------------------------
    // Negative: prove the test is not a no-op.
    // ------------------------------------------------------------------

    [Test]
    procedure FieldGroup_NotZero()
    var
        Rec: Record "AFG Base Table";
        Src: Codeunit "AFG Field Group Src";
    begin
        // [GIVEN] A record with Id = 7
        Rec.Id := 7;
        // [THEN]  The value is NOT zero — a no-op mock returning 0 would fail
        Assert.AreNotEqual(0, Src.GetId(Rec), 'Id must not be zero');
    end;

    [Test]
    procedure FieldGroup_EmptyNameIsEmpty()
    var
        Rec: Record "AFG Base Table";
        Src: Codeunit "AFG Field Group Src";
    begin
        // [GIVEN] A record with Name not set (default empty)
        // [THEN]  GetName returns an empty string — proves default initialisation works
        Assert.AreEqual('', Src.GetName(Rec), 'Default name must be empty');
    end;
}
