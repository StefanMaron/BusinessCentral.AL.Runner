codeunit 109002 FieldRefMetaTest
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    [Test]
    procedure TestFieldRefNameQuotedField()
    var
        Src: Codeunit FieldRefMetaSrc;
        FieldName: Text;
    begin
        // Field 1 of our AL-compiled table has quoted name "Document No."
        FieldName := Src.GetFieldName(109000, 1);
        Assert.AreEqual('Document No.', FieldName, 'FieldRef.Name should return actual quoted field name, not stub');
    end;

    [Test]
    procedure TestFieldRefNameBareField()
    var
        Src: Codeunit FieldRefMetaSrc;
        FieldName: Text;
    begin
        // Field 2 has bare name Description
        FieldName := Src.GetFieldName(109000, 2);
        Assert.AreEqual('Description', FieldName, 'FieldRef.Name should return bare field name');
    end;

    [Test]
    procedure TestFieldRefCaptionExplicit()
    var
        Src: Codeunit FieldRefMetaSrc;
        Caption: Text;
    begin
        // Field 2 has Caption = 'Description Caption'
        Caption := Src.GetFieldCaption(109000, 2);
        Assert.AreEqual('Description Caption', Caption, 'FieldRef.Caption should return explicit caption');
    end;

    [Test]
    procedure TestFieldRefCaptionFallsBackToName()
    var
        Src: Codeunit FieldRefMetaSrc;
        Caption: Text;
    begin
        // Field 3 "Amount" has no explicit Caption — should fall back to field name
        Caption := Src.GetFieldCaption(109000, 3);
        Assert.AreEqual('Amount', Caption, 'FieldRef.Caption should fall back to field name when no caption declared');
    end;

    [Test]
    procedure TestFieldRefNameNotStub()
    var
        Src: Codeunit FieldRefMetaSrc;
        FieldName: Text;
    begin
        // Verify it does not return the stub "Field1" pattern
        FieldName := Src.GetFieldName(109000, 1);
        Assert.AreNotEqual('Field1', FieldName, 'FieldRef.Name must not return stub value "Field1"');
    end;

    [Test]
    procedure TestFieldRefNameByIndex()
    var
        Src: Codeunit FieldRefMetaSrc;
        FieldName: Text;
    begin
        // FieldIndex(1) is the 1st field by ordinal position (field 1 = "Document No.")
        FieldName := Src.GetFieldNameByIndex(109000, 1);
        Assert.AreEqual('Document No.', FieldName, 'FieldRef.Name via FieldIndex should return actual field name, not stub');
    end;

    [Test]
    procedure TestTableExtensionFieldName()
    var
        Src: Codeunit FieldRefMetaSrc;
        FieldName: Text;
    begin
        // Field 10 is added by tableextension; Name should be "Extended Field"
        FieldName := Src.GetFieldName(109000, 10);
        Assert.AreEqual('Extended Field', FieldName, 'FieldRef.Name for tableextension field should return actual name, not stub');
    end;

    [Test]
    procedure TestTableExtensionFieldCaption()
    var
        Src: Codeunit FieldRefMetaSrc;
        Caption: Text;
    begin
        // Field 10 from tableextension has Caption = 'Extended Field Caption'
        Caption := Src.GetFieldCaption(109000, 10);
        Assert.AreEqual('Extended Field Caption', Caption, 'FieldRef.Caption for tableextension field should return actual caption');
    end;
}
