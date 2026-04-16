/// Exercises NavApp archive/resource stubs:
/// GetArchiveVersion, GetArchiveRecordRef, LoadPackageData,
/// RestoreArchiveData, DeleteArchiveData, GetResource.
codeunit 94000 "NAA Src"
{
    procedure GetArchiveVersion(AppId: Guid): Text
    begin
        exit(NavApp.GetArchiveVersion(AppId));
    end;

    procedure CheckGetArchiveRecordRef(AppId: Guid; var RecRef: RecordRef)
    begin
        NavApp.GetArchiveRecordRef(AppId, RecRef);
    end;

    procedure CallLoadPackageData(TableId: Integer)
    begin
        NavApp.LoadPackageData(TableId);
    end;

    procedure CallRestoreArchiveData(TableId: Integer)
    begin
        NavApp.RestoreArchiveData(TableId);
    end;

    procedure CallDeleteArchiveData(TableId: Integer)
    begin
        NavApp.DeleteArchiveData(TableId);
    end;

    procedure CheckGetResource(ResourceName: Text[250]; var IStream: InStream)
    begin
        NavApp.GetResource(ResourceName, IStream);
    end;
}
