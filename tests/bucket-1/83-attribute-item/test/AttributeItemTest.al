codeunit 82001 "AI Attribute Item Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Lib: Codeunit "AI Attribute Item Lib";

    [Test]
    procedure TableWithDataClassificationCompiles()
    var
        Rec: Record "AI Test Record";
    begin
        // [GIVEN] A table with DataClassification on the table and fields
        // [WHEN] The table is initialized and inserted
        Rec.Init();
        Rec.Id := 1;
        Rec.Name := 'TestName';
        Rec.Insert();
        // [THEN] Record count is 1 — DataClassification did not block compilation/execution
        Assert.AreEqual(1, Rec.Count(), 'Table with DataClassification must compile and insert');
    end;

    [Test]
    procedure FieldsWithVariousDataClassificationsWork()
    var
        Rec: Record "AI Test Record";
    begin
        // [GIVEN] A table with fields having different DataClassification values
        Rec.Init();
        Rec.Id := 2;
        Rec.Name := 'Alice';
        Rec.Amount := 42.5;
        Rec.Active := true;
        Rec.Insert();
        // [WHEN] The record is retrieved
        Rec.Get(2);
        // [THEN] All field values are correct — DataClassification is metadata only
        Assert.AreEqual('Alice', Rec.Name, 'Name field with EUII classification must round-trip');
        Assert.AreEqual(42.5, Rec.Amount, 'Amount field with OII classification must round-trip');
        Assert.IsTrue(Rec.Active, 'Active field with ToBeClassified must round-trip');
    end;

    [Test]
    procedure FieldWithObsoleteStatePendingCompiles()
    var
        Rec: Record "AI Test Record";
    begin
        // [GIVEN] A table field with ObsoleteState = Pending and ObsoleteReason
        // [WHEN] The record is inserted with Notes set
        Rec.Init();
        Rec.Id := 3;
        Rec.Name := 'ObsoleteTest';
        Rec.Notes := 'some notes';
        Rec.Insert();
        // [THEN] The obsolete field still works — ObsoleteState is metadata only
        Rec.Get(3);
        Assert.AreEqual('some notes', Rec.Notes, 'Obsolete-pending field must still be readable');
    end;

    [Test]
    procedure TableExtensionWithDataClassificationCompiles()
    var
        Rec: Record "AI Test Record";
    begin
        // [GIVEN] A tableextension that adds a field with DataClassification = ToBeClassified
        // [WHEN] The extended field is set and inserted
        Rec.Init();
        Rec.Id := 4;
        Rec.Name := 'ExtTest';
        Rec.Code := 'ABC123';
        Rec.Insert();
        // [THEN] The extension field persists correctly
        Rec.Get(4);
        Assert.AreEqual('ABC123', Rec.Code, 'Table extension field with DataClassification must round-trip');
    end;

    [Test]
    procedure CannnotInsertDuplicatePK()
    var
        Rec: Record "AI Test Record";
    begin
        // [GIVEN] A record already exists with Id = 5
        Rec.Init();
        Rec.Id := 5;
        Rec.Insert();
        // [WHEN] A second insert with the same PK is attempted
        // [THEN] An error is raised
        asserterror Rec.Insert();
        Assert.ExpectedError('already exists');
    end;
}
