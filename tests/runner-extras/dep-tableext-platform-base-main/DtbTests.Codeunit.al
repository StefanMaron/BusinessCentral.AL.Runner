/// <summary>
/// Regression suite for #1686: "Platform-app table metadata lost when a dependency
/// app extends the table". The extension in "DTB Platform Base Dep" adds two fields
/// to the platform Item table (27) — a table whose OWN base metadata is served from
/// the precompiled Base Application .app, never from AL source in either bundle.
///
/// RED (without the fix): every test below throws
///   InvalidOperationException: NavRecordHandle.CreateTarget: no NCLMetaTable for
///   table 27 (AL source not parsed)
/// — actually a duplicated-field NullReferenceException inside
/// NCLMetaTable.SetSystemFields(), caused by BuildSiblingSourceDeps registering the
/// dependency's source dir twice (see RecordPatches.AddSourceDir / .AlSourceParser.cs).
///
/// GREEN (with the fix): both extension fields round-trip through Record Item exactly
/// like fields declared in the same bundle would.
/// </summary>
codeunit 63411 "DTB TableExt Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "DTB Assert";

    /// <summary>
    /// Positive: a Boolean field from a dependency-declared tableextension on the
    /// platform Item table round-trips through Insert + Get.
    /// </summary>
    [Test]
    procedure ItemRoundtrip_BooleanExtensionField_ReturnsInsertedValue()
    var
        Item: Record Item;
    begin
        Item.Init();
        Item."No." := 'DTB-BOOL-1';
        Item."DTB Repro Flag" := true;
        Item.Insert();

        Item.Get('DTB-BOOL-1');

        Assert.AreEqual(true, Item."DTB Repro Flag",
            'Boolean extension field must round-trip through Insert/Get exactly like an own-bundle field');
    end;

    /// <summary>
    /// Positive: an Integer field from the same dependency tableextension round-trips
    /// with a non-default value. Guards against a stub implementation that would only
    /// ever satisfy the Boolean case above (e.g. by always returning the type default).
    /// </summary>
    [Test]
    procedure ItemRoundtrip_IntegerExtensionField_ReturnsInsertedValue()
    var
        Item: Record Item;
    begin
        Item.Init();
        Item."No." := 'DTB-INT-1';
        Item."DTB Repro Counter" := 57;
        Item.Insert();

        Item.Get('DTB-INT-1');

        Assert.AreEqual(57, Item."DTB Repro Counter",
            'Integer extension field must round-trip through Insert/Get with a concrete non-default value');
    end;

    /// <summary>
    /// Positive: Init() leaves the dependency-declared extension fields at their AL
    /// type defaults (false / 0) until explicitly assigned — proves the fields are
    /// really wired into the record's field list (not silently absent, which would
    /// otherwise leave the record uninitialized rather than zero-valued).
    /// </summary>
    [Test]
    procedure ItemInit_ExtensionFieldsDefaultToTypeDefaults()
    var
        Item: Record Item;
    begin
        Item.Init();

        Assert.AreEqual(false, Item."DTB Repro Flag",
            'Init() must leave the Boolean extension field at its type default (false)');
        Assert.AreEqual(0, Item."DTB Repro Counter",
            'Init() must leave the Integer extension field at its type default (0)');
    end;

    /// <summary>
    /// Negative: Get() on a "No." that was never inserted still raises BC's real
    /// "does not exist" error on the platform Item table — proving table 27's
    /// NCLMetaTable (with the dependency's extension fields merged in) is the real
    /// BC record-not-found path, not a fake that swallows the lookup miss.
    /// </summary>
    [Test]
    procedure ItemGet_NonExistentNo_RaisesDoesNotExistError()
    var
        Item: Record Item;
    begin
        asserterror Item.Get('DTB-DOES-NOT-EXIST');

        Assert.ExpectedErrorContains(GetLastErrorText(), 'does not exist',
            'Get() on a missing key must raise BC''s real record-not-found error');
    end;
}
