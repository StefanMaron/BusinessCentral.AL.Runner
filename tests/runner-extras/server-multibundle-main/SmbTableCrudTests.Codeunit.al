/// <summary>
/// Proves that record CRUD on a table defined in a DEPENDENCY app's AL source
/// works when the app + test-app pair runs as two bundles of one invocation —
/// both on the CLI and (via scripts/tests/server-mode-test.sh) through
/// --server, where a per-bundle cache reset used to wipe the dep bundle's
/// parsed table schema before this bundle ran and every test here died with
/// "NavRecordHandle.CreateTarget: no NCLMetaTable for table 64300 (AL source
/// not parsed)".
///
/// RED (without fix): every test fails with the NCLMetaTable error under --server.
/// GREEN (with fix): concrete inserted values read back on both paths.
/// </summary>
codeunit 64351 "SMB Table CRUD Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "SMB Assert";

    /// <summary>
    /// Positive: Insert then Get reads back the exact field values written —
    /// proves the dep table's fields are materialized from its AL source, not
    /// defaulted.
    /// </summary>
    [Test]
    procedure Insert_ThenGet_ReadsBackConcreteValues()
    var
        Item: Record "SMB Item";
    begin
        Item.Init();
        Item."Code" := 'ALPHA';
        Item.Description := 'First item';
        Item.Quantity := 5;
        Item.Insert();

        Item.Get('ALPHA');
        Assert.AreEqualText('First item', Item.Description, 'Description must round-trip through Insert/Get');
        Assert.AreEqual(5, Item.Quantity, 'Quantity must round-trip through Insert/Get');
    end;

    /// <summary>
    /// Positive: Modify persists the new value — a re-Get returns 42, not the
    /// originally inserted 1.
    /// </summary>
    [Test]
    procedure Modify_PersistsNewQuantity()
    var
        Item: Record "SMB Item";
    begin
        Item.Init();
        Item."Code" := 'BETA';
        Item.Quantity := 1;
        Item.Insert();

        Item.Quantity := 42;
        Item.Modify();

        Item.Get('BETA');
        Assert.AreEqual(42, Item.Quantity, 'Modify must persist Quantity=42 over the inserted 1');
    end;

    /// <summary>
    /// Positive: Delete removes the row — a subsequent Get returns the concrete
    /// value FALSE (not an error), proving the row is gone rather than unreadable.
    /// </summary>
    [Test]
    procedure Delete_ThenGet_ReturnsFalse()
    var
        Item: Record "SMB Item";
    begin
        Item.Init();
        Item."Code" := 'GAMMA';
        Item.Insert();

        Item.Delete();
        Assert.IsFalse(Item.Get('GAMMA'), 'Get after Delete must return false');
    end;

    /// <summary>
    /// Negative: inserting the same primary key twice fails with the specific
    /// "already exists" error — proves key enforcement runs against the dep
    /// table's real schema instead of silently overwriting.
    /// </summary>
    [Test]
    procedure DuplicateInsert_FailsWithAlreadyExists()
    var
        Item: Record "SMB Item";
    begin
        Item.Init();
        Item."Code" := 'DUP';
        Item.Insert();

        Item.Init();
        Item."Code" := 'DUP';
        asserterror Item.Insert();
        Assert.ExpectedError('already exists');
    end;
}
