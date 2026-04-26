codeunit 60111 "LF Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "LF Src";

    [Test]
    procedure AddLoadFields_DoesNotThrow()
    begin
        Assert.IsTrue(Src.AddLoadFieldsDoesNotThrow(),
            'AddLoadFields must complete without throwing');
    end;

    [Test]
    procedure DataReadsAfterLoadFields_Work()
    begin
        // Proves that AddLoadFields is a no-op: every field stays reachable
        // regardless of the load-hint API.
        Assert.AreEqual('Alice/Paris', Src.DataRoundTripAfterLoadFields(),
            'Field reads must work after SetLoadFields + AddLoadFields');
    end;

    [Test]
    procedure AddLoadFieldsMultiple_FieldReachable()
    begin
        // Standalone contract: "City" is reachable even though it was never
        // added to the load set — all fields are always in memory.
        Assert.AreEqual('Berlin', Src.AddLoadFieldsMultiple_DataIntact(),
            'Fields not named via AddLoadFields must still be reachable');
    end;

    [Test]
    procedure AddLoadFields_NotCorruptingFilterState()
    begin
        // Filter + count after AddLoadFields — verifies the record remains
        // in a usable state with filters intact.
        Assert.AreEqual(3, Src.AddLoadFields_AfterSet_NotOverridden(),
            'Filtered Count() after AddLoadFields must include matching rows');
    end;

    [Test]
    procedure RecRef_AreFieldsLoaded_AllFields_True()
    begin
        // Positive: standalone contract — every field is always loaded.
        Assert.IsTrue(Src.RecRefAreFieldsLoaded_ReturnsTrue(),
            'RecordRef.AreFieldsLoaded must return true in standalone mode');
    end;

    [Test]
    procedure RecRef_AreFieldsLoaded_WithAddLoadFields()
    begin
        // Even a field never added to the load set reports as loaded, because
        // partial loading is not modelled standalone.
        Assert.IsTrue(Src.RecRefAreFieldsLoaded_AfterSetLoadFields(),
            'RecordRef.AreFieldsLoaded must remain true even for fields not in the load set');
    end;

    [Test]
    procedure SetLoadFieldsOnSelf_NoThrow()
    begin
        Src.DriveSetLoadFieldsOnSelf();
    end;

    [Test]
    procedure AddLoadFieldsOnSelf_NoThrow()
    begin
        Src.DriveAddLoadFieldsOnSelf();
    end;

    [Test]
    procedure AreFieldsLoadedOnSelf_ReturnsTrue()
    begin
        Assert.IsTrue(Src.DriveAreFieldsLoadedOnSelf(),
            'AreFieldsLoaded on Self must return true (all fields always loaded standalone)');
    end;
}
