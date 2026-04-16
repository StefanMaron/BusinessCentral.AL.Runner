/// Exercises NavApp archive/resource stubs:
/// GetArchiveVersion, LoadPackageData, RestoreArchiveData, DeleteArchiveData.
/// GetArchiveRecordRef and GetResource require types (RecordRef/InStream)
/// that are complex to test here; separate follow-up.
codeunit 60280 "NAR Src"
{
    procedure GetArchiveVersion(): Text
    begin
        exit(NavApp.GetArchiveVersion());
    end;

    procedure LoadPackageData_DoesNotThrow(): Boolean
    begin
        NavApp.LoadPackageData(0);
        exit(true);
    end;

    procedure RestoreArchiveData_DoesNotThrow(): Boolean
    begin
        NavApp.RestoreArchiveData(0);
        exit(true);
    end;

    procedure DeleteArchiveData_DoesNotThrow(): Boolean
    begin
        NavApp.DeleteArchiveData(0);
        exit(true);
    end;
}
