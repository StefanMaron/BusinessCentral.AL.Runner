codeunit 50118 "File Stream Helper"
{
    procedure TestUploadWithFileName(): Boolean
    var
        InStr: InStream;
        FileName: Text;
    begin
        // UploadIntoStream with dialog title, initial folder, filter, filename, instream
        exit(UploadIntoStream('Upload', '', '*.txt', FileName, InStr));
    end;

    procedure TestDownloadFromStream(): Boolean
    var
        InStr: InStream;
        FileName: Text;
    begin
        // DownloadFromStream — tries to download using an InStream
        exit(DownloadFromStream(InStr, 'Download', '', '*.txt', FileName));
    end;
}
