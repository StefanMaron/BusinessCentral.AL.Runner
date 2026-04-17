codeunit 124001 "MSM Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // ── Count ─────────────────────────────────────────────────────────────────

    [Test]
    procedure Count_ReturnsZero_WhenEmpty()
    var
        Rec: Record "MSM Table";
    begin
        // Positive: Count() returns 0 for a newly initialised record.
        Rec.Init();
        Rec."No." := 'C1';
        Rec.Insert();
        Assert.AreEqual(0, Rec.Picture.Count(), 'Empty MediaSet must return 0');
    end;

    [Test]
    procedure Count_ReturnsOne_AfterInsert()
    var
        Rec: Record "MSM Table";
        Id: Guid;
    begin
        // Positive: Count() returns 1 after one Insert.
        Rec.Init();
        Rec."No." := 'C2';
        Rec.Insert();
        Id := CreateGuid();
        Rec.Picture.Insert(Id);
        Assert.AreEqual(1, Rec.Picture.Count(), 'Count must be 1 after Insert');
    end;

    // ── Insert ────────────────────────────────────────────────────────────────

    [Test]
    procedure Insert_ReturnsTrue()
    var
        Rec: Record "MSM Table";
        Id: Guid;
        Result: Boolean;
    begin
        // Positive: Insert returns true.
        Rec.Init();
        Rec."No." := 'I1';
        Rec.Insert();
        Id := CreateGuid();
        Result := Rec.Picture.Insert(Id);
        Assert.IsTrue(Result, 'Insert must return true');
    end;

    // ── Remove ────────────────────────────────────────────────────────────────

    [Test]
    procedure Remove_ReturnsTrue_WhenItemPresent()
    var
        Rec: Record "MSM Table";
        Id: Guid;
        Result: Boolean;
    begin
        // Positive: Remove returns true when the GUID was previously inserted.
        Rec.Init();
        Rec."No." := 'R1';
        Rec.Insert();
        Id := CreateGuid();
        Rec.Picture.Insert(Id);
        Result := Rec.Picture.Remove(Id);
        Assert.IsTrue(Result, 'Remove must return true for an inserted item');
    end;

    [Test]
    procedure Remove_ReturnsFalse_WhenItemAbsent()
    var
        Rec: Record "MSM Table";
        Id: Guid;
        Result: Boolean;
    begin
        // Negative: Remove returns false when the GUID is not in the set.
        Rec.Init();
        Rec."No." := 'R2';
        Rec.Insert();
        Id := CreateGuid();
        Result := Rec.Picture.Remove(Id);
        Assert.IsFalse(Result, 'Remove must return false for an absent item');
    end;

    [Test]
    procedure Remove_DecreasesCount()
    var
        Rec: Record "MSM Table";
        Id: Guid;
    begin
        // Positive: Count decreases by 1 after Remove.
        Rec.Init();
        Rec."No." := 'R3';
        Rec.Insert();
        Id := CreateGuid();
        Rec.Picture.Insert(Id);
        Rec.Picture.Remove(Id);
        Assert.AreEqual(0, Rec.Picture.Count(), 'Count must be 0 after Remove');
    end;

    // ── Item ──────────────────────────────────────────────────────────────────

    [Test]
    procedure Item_ReturnsInsertedGuid()
    var
        Rec: Record "MSM Table";
        Id: Guid;
        Retrieved: Guid;
    begin
        // Positive: Item(1) returns the GUID that was inserted first.
        Rec.Init();
        Rec."No." := 'IT1';
        Rec.Insert();
        Id := CreateGuid();
        Rec.Picture.Insert(Id);
        Retrieved := Rec.Picture.Item(1);
        Assert.AreEqual(Id, Retrieved, 'Item(1) must return the inserted GUID');
    end;

    // ── MediaId ───────────────────────────────────────────────────────────────

    [Test]
    procedure MediaId_ReturnsNonEmptyGuid()
    var
        Rec: Record "MSM Table";
        Id: Guid;
    begin
        // Positive: MediaId() returns a non-empty GUID identifying the set.
        Rec.Init();
        Rec."No." := 'M1';
        Rec.Insert();
        Id := Rec.Picture.MediaId();
        Assert.IsFalse(IsNullGuid(Id), 'MediaId must return a non-empty GUID');
    end;

    // ── ImportFile ────────────────────────────────────────────────────────────

    [Test]
    procedure ImportFile_ReturnsNonEmptyGuid()
    var
        Rec: Record "MSM Table";
        Result: Guid;
    begin
        // Positive: ImportFile returns a non-empty GUID identifying the imported media.
        // (BC's MediaSet.ImportFile returns the new media GUID, not a Boolean.)
        Rec.Init();
        Rec."No." := 'IF1';
        Rec.Insert();
        Result := Rec.Picture.ImportFile('photo.jpg', 'My Photo');
        Assert.IsFalse(IsNullGuid(Result), 'ImportFile must return a non-empty GUID');
    end;

    // ── ExportFile ────────────────────────────────────────────────────────────

    [Test]
    procedure ExportFile_ReturnsZero_WhenNoData()
    var
        Rec: Record "MSM Table";
        Result: Integer;
    begin
        // Negative: ExportFile returns 0 — no blob data available in standalone mode.
        // (BC's MediaSet.ExportFile returns an Integer count, not a Boolean.)
        Rec.Init();
        Rec."No." := 'EF1';
        Rec.Insert();
        Result := Rec.Picture.ExportFile('out.jpg');
        Assert.AreEqual(0, Result, 'ExportFile must return 0 (no data)');
    end;
}
