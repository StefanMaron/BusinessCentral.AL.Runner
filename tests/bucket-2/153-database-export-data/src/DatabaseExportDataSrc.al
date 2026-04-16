/// Helper that calls Database.ExportData — must be a no-op stub in the runner
/// since file I/O is not available without a BC service tier.
codeunit 50153 "DED Helper"
{
    /// Call Database.ExportData with a filename — runner must not throw.
    procedure CallExportData(FileName: Text)
    begin
        Database.ExportData(FileName);
    end;

    /// Prove helper itself is callable (ensures compilation unit is live).
    procedure Add(a: Integer; b: Integer): Integer
    begin
        exit(a + b);
    end;
}
