codeunit 1259002 "Media Id Arg Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure MediaId_AsArgument_Compiles()
    var
        Rec: Record "Media Id Arg Table";
        Helper: Codeunit "Media Id Arg Src";
        Id: Guid;
    begin
        // Positive: MediaId() result can be passed as a Guid argument.
        // This reproduces the CS1503 "method group to object" error from issue #1259.
        Rec.Init();
        Rec."No." := 'A1';
        Rec.Insert();
        Id := Helper.GetGuidBack(Rec.Image.MediaId());
        Assert.IsFalse(IsNullGuid(Id), 'MediaId passed as arg must return non-empty GUID');
    end;

    [Test]
    procedure MediaId_Standalone_ReturnsNonEmpty()
    var
        Rec: Record "Media Id Arg Table";
        Id: Guid;
    begin
        // Positive: MediaId() standalone returns a non-empty GUID.
        Rec.Init();
        Rec."No." := 'A2';
        Rec.Insert();
        Id := Rec.Image.MediaId();
        Assert.IsFalse(IsNullGuid(Id), 'MediaId must return a non-empty GUID');
    end;
}
