codeunit 84101 "FROM Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "FROM Src";

    [Test]
    procedure OptionMembers_OptionField_ReturnsMembers()
    begin
        Assert.AreEqual('Open,Released,Closed', Src.OptionMembersOnOptionField(),
            'OptionMembers on Option field must return comma-separated member names');
    end;

    [Test]
    procedure OptionMembers_TextField_ReturnsEmpty()
    begin
        Assert.AreEqual('', Src.OptionMembersOnTextField(),
            'OptionMembers on Text field must return empty string');
    end;

    [Test]
    procedure IsOptimizedForTextSearch_ReturnsFalse()
    begin
        Assert.IsFalse(Src.IsTextSearchOptimized(),
            'IsOptimizedForTextSearch must return false (no full-text index in standalone mode)');
    end;

    [Test]
    procedure OptionMembers_NotEmpty_ForOptionField()
    var
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        RecRef.Open(Database::"FROM Test Table");
        FRef := RecRef.Field(2);
        Assert.AreNotEqual('', FRef.OptionMembers, 'OptionMembers on Option field must not be empty');
    end;
}
