/// Helper codeunit exercising FileUpload.FileName() and FileUpload.CreateInStream().
/// In standalone mode the default FileUpload has no file data or name.
codeunit 86000 "FU Src"
{
    procedure GetFileName(Upload: FileUpload): Text
    begin
        exit(Upload.FileName());
    end;

    procedure CreateStreamAndCheckEOS(Upload: FileUpload): Boolean
    var
        InStr: InStream;
    begin
        Upload.CreateInStream(InStr);
        exit(InStr.EOS());
    end;

    procedure CreateStreamWithEncoding(Upload: FileUpload): Boolean
    var
        InStr: InStream;
    begin
        Upload.CreateInStream(InStr, TextEncoding::UTF8);
        exit(InStr.EOS());
    end;
}
