/// Helper codeunit exercising Media methods via table field access.
codeunit 84407 "Media Src"
{
    procedure GetHasValue(var Rec: Record "Media Test Storage"): Boolean
    begin
        exit(Rec.MediaField.HasValue());
    end;

    procedure ImportFileOnRec(var Rec: Record "Media Test Storage"; FileName: Text; Desc: Text): Guid
    begin
        exit(Rec.MediaField.ImportFile(FileName, Desc));
    end;

    procedure ExportFileFromRec(var Rec: Record "Media Test Storage"; FileName: Text): Boolean
    begin
        exit(Rec.MediaField.ExportFile(FileName));
    end;
}
