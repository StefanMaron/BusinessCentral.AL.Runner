/// Exercises NavApp archive/resource stubs:
/// GetArchiveVersion, GetArchiveRecordRef, LoadPackageData,
/// RestoreArchiveData, DeleteArchiveData, GetResource.
codeunit 97000 "NAA Src"
{
    procedure GetArchiveVersion(): Text
    begin
        exit(NavApp.GetArchiveVersion());
    end;

    procedure CheckGetArchiveRecordRef(TableId: Integer; var RecRef: RecordRef)
    begin
        NavApp.GetArchiveRecordRef(TableId, RecRef);
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
