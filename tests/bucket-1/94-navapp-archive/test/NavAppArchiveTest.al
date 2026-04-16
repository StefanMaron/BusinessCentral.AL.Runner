codeunit 94001 "NAA Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "NAA Src";

    // ------------------------------------------------------------------
    // GetArchiveVersion — returns empty string (no archive in runner)
    // ------------------------------------------------------------------

    [Test]
    procedure NavApp_GetArchiveVersion_ReturnsEmpty()
    var
        AppId: Guid;
    begin
        AppId := CreateGuid();
        Assert.AreEqual('', Src.GetArchiveVersion(AppId),
            'GetArchiveVersion should return empty string in standalone');
    end;

    // ------------------------------------------------------------------
    // GetArchiveRecordRef — no-op; RecordRef stays unbound (Number = 0)
    // ------------------------------------------------------------------

    [Test]
    procedure NavApp_GetArchiveRecordRef_LeavesUnbound()
    var
        AppId: Guid;
        RecRef: RecordRef;
    begin
        AppId := CreateGuid();
        Src.CheckGetArchiveRecordRef(AppId, RecRef);
        Assert.AreEqual(0, RecRef.Number(), 'GetArchiveRecordRef should leave RecordRef unbound');
    end;

    // ------------------------------------------------------------------
    // LoadPackageData — no-op; must not throw
    // ------------------------------------------------------------------

    [Test]
    procedure NavApp_LoadPackageData_IsNoOp()
    begin
        Src.CallLoadPackageData(0);
    end;

    // ------------------------------------------------------------------
    // RestoreArchiveData — no-op; must not throw
    // ------------------------------------------------------------------

    [Test]
    procedure NavApp_RestoreArchiveData_IsNoOp()
    begin
        Src.CallRestoreArchiveData(0);
    end;

    // ------------------------------------------------------------------
    // DeleteArchiveData — no-op; must not throw
    // ------------------------------------------------------------------

    [Test]
    procedure NavApp_DeleteArchiveData_IsNoOp()
    begin
        Src.CallDeleteArchiveData(0);
    end;

    // ------------------------------------------------------------------
    // GetResource — must not throw; stub is a no-op in standalone
    // ------------------------------------------------------------------

    [Test]
    procedure NavApp_GetResource_IsNoOp()
    var
        IStream: InStream;
    begin
        Src.CheckGetResource('nonexistent.txt', IStream);
    end;
}
