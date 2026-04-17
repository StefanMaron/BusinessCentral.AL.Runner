codeunit 107001 "TMI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TMI Src";

    // ── FieldCaption ────────────────────────────────────────────────────────────

    [Test]
    procedure FieldCaption_Id_ReturnsId()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreEqual('Id', Src.FieldCaptionId(Rec), 'FieldCaption for "Id" field must return "Id"');
    end;

    [Test]
    procedure FieldCaption_Name_ReturnsName()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreEqual('Name', Src.FieldCaptionName(Rec), 'FieldCaption for "Name" field must return "Name"');
    end;

    [Test]
    procedure FieldCaption_Amount_ReturnsAmount()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreEqual('Amount', Src.FieldCaptionAmount(Rec), 'FieldCaption for "Amount" field must return "Amount"');
    end;

    [Test]
    procedure FieldCaption_DiffersForDifferentFields()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreNotEqual(Src.FieldCaptionId(Rec), Src.FieldCaptionName(Rec),
            'FieldCaption must differ for different fields');
    end;

    // ── FieldName ───────────────────────────────────────────────────────────────

    [Test]
    procedure FieldName_Id_ReturnsId()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreEqual('Id', Src.FieldNameId(Rec), 'FieldName for "Id" field must return "Id"');
    end;

    [Test]
    procedure FieldName_Name_ReturnsName()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreEqual('Name', Src.FieldNameName(Rec), 'FieldName for "Name" field must return "Name"');
    end;

    // ── FieldActive ─────────────────────────────────────────────────────────────

    [Test]
    procedure FieldActive_IdField_ReturnsTrue()
    var
        Rec: Record "TMI Record";
    begin
        Assert.IsTrue(Src.IsFieldIdActive(Rec), 'FieldActive must return true for existing "Id" field');
    end;

    [Test]
    procedure FieldActive_NameField_ReturnsTrue()
    var
        Rec: Record "TMI Record";
    begin
        Assert.IsTrue(Src.IsFieldNameActive(Rec), 'FieldActive must return true for existing "Name" field');
    end;

    // ── FieldError ──────────────────────────────────────────────────────────────

    [Test]
    procedure FieldError_ThrowsError()
    var
        Rec: Record "TMI Record";
    begin
        asserterror Src.TriggerFieldError(Rec);
        Assert.ExpectedError('');
    end;

    [Test]
    procedure FieldError_WithMessage_ContainsMessage()
    var
        Rec: Record "TMI Record";
    begin
        asserterror Src.TriggerFieldErrorWithMessage(Rec, 'must not be blank');
        Assert.ExpectedError('must not be blank');
    end;

    // ── TableCaption ────────────────────────────────────────────────────────────

    [Test]
    procedure TableCaption_ReturnsNonEmpty()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreNotEqual('', Src.GetTableCaption(Rec), 'TableCaption must return non-empty string');
    end;

    [Test]
    procedure TableCaption_ContainsTableIdentifier()
    var
        Rec: Record "TMI Record";
        Caption: Text;
    begin
        Caption := Src.GetTableCaption(Rec);
        Assert.IsTrue(Caption.Contains('TMI'), 'TableCaption must contain table identifier "TMI"');
    end;

    // ── TableName ───────────────────────────────────────────────────────────────

    [Test]
    procedure TableName_ReturnsTMIRecord()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreEqual('TMI Record', Src.GetTableName(Rec), 'TableName must return "TMI Record"');
    end;

    [Test]
    procedure TableName_DiffersFromFieldName()
    var
        Rec: Record "TMI Record";
    begin
        // Table name must not equal a field name
        Assert.AreNotEqual(Src.GetTableName(Rec), Src.FieldNameId(Rec),
            'TableName must not equal a field name');
    end;

    // ── CurrentKey ──────────────────────────────────────────────────────────────

    [Test]
    procedure CurrentKey_DefaultContainsPkField()
    var
        Rec: Record "TMI Record";
        CurKey: Text;
    begin
        CurKey := Src.GetCurrentKey(Rec);
        // Default sort is by PK (field "Id")
        Assert.IsTrue(CurKey.Contains('Id'), 'CurrentKey must contain PK field "Id" by default');
    end;

    [Test]
    procedure CurrentKey_NotEmpty()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreNotEqual('', Src.GetCurrentKey(Rec), 'CurrentKey must return non-empty string');
    end;

    // ── CurrentCompany ──────────────────────────────────────────────────────────

    [Test]
    procedure CurrentCompany_ReturnsNonEmpty()
    var
        Rec: Record "TMI Record";
    begin
        Assert.AreNotEqual('', Src.GetCurrentCompany(Rec), 'CurrentCompany must return non-empty string');
    end;

    // ── IsTemporary ─────────────────────────────────────────────────────────────

    [Test]
    procedure IsTemporary_RegularRecord_ReturnsFalse()
    var
        Rec: Record "TMI Record";
    begin
        Assert.IsFalse(Src.CheckIsTemporary(Rec), 'IsTemporary must return false for a regular record');
    end;

    [Test]
    procedure IsTemporary_TempRecord_ReturnsTrue()
    var
        Rec: Record "TMI Record" temporary;
    begin
        Assert.IsTrue(Src.CheckTempIsTemporary(Rec), 'IsTemporary must return true for a temporary record');
    end;

    // ── ReadPermission ──────────────────────────────────────────────────────────

    [Test]
    procedure ReadPermission_ReturnsTrue()
    var
        Rec: Record "TMI Record";
    begin
        Assert.IsTrue(Src.HasReadPermission(Rec), 'ReadPermission must return true in standalone runner');
    end;

    // ── WritePermission ─────────────────────────────────────────────────────────

    [Test]
    procedure WritePermission_ReturnsTrue()
    var
        Rec: Record "TMI Record";
    begin
        Assert.IsTrue(Src.HasWritePermission(Rec), 'WritePermission must return true in standalone runner');
    end;

    // ── RecordLevelLocking ──────────────────────────────────────────────────────

    [Test]
    procedure RecordLevelLocking_ReturnsFalse()
    var
        Rec: Record "TMI Record";
    begin
        Assert.IsFalse(Src.GetRecordLevelLocking(Rec), 'RecordLevelLocking must return false in standalone runner');
    end;

    // ── RecordId ────────────────────────────────────────────────────────────────

    [Test]
    procedure RecordId_DoesNotCrash()
    var
        Rec: Record "TMI Record";
    begin
        // RecordId() must not throw — standalone returns a default RecordId value
        Assert.IsTrue(Src.GetRecordIdDoesNotCrash(Rec), 'RecordId() must not crash');
    end;
}
