/// Helper codeunit exercising File I/O methods in standalone mode.
/// All file operations are in-memory; no real filesystem is touched.
codeunit 87000 "FIO Src"
{
    procedure FileExists(FileName: Text): Boolean
    begin
        exit(File.Exists(FileName));
    end;

    procedure WriteAndReadBack(Content: Text): Text
    var
        f: File;
        ReadBack: Text;
    begin
        f.TextMode(true);
        f.WriteMode(true);
        f.Create('test.tmp');
        f.Write(Content);
        f.Seek(0);
        f.Read(ReadBack);
        f.Close();
        exit(ReadBack);
    end;

    procedure CreateAndLen(Content: Text): Integer
    var
        f: File;
    begin
        f.TextMode(true);
        f.WriteMode(true);
        f.Create('len.tmp');
        f.Write(Content);
        exit(f.Len());
    end;
}
