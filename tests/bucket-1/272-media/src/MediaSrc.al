/// Helper codeunit exercising Media methods via table field access.
codeunit 84407 "Media Src"
{
    // Minimal stub — just ImportFile which returns Boolean, no HasValue
    procedure ImportFileReturnsTrue(FileName: Text): Boolean
    var
        Storage: Record "Media Test Storage" temporary;
        Ok: Boolean;
    begin
        Ok := Storage.MediaField.ImportFile(FileName, '');
        exit(Ok);
    end;

    procedure ExportFileOnDefaultReturnsFalse(FileName: Text): Boolean
    var
        Storage: Record "Media Test Storage" temporary;
        Ok: Boolean;
    begin
        Ok := Storage.MediaField.ExportFile(FileName);
        exit(Ok);
    end;

}
