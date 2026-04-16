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
        OutStr: OutStream;
        InStr: InStream;
        ReadBack: Text;
    begin
        f.Create('test.tmp');
        f.CreateOutStream(OutStr);
        OutStr.WriteText(Content);
        f.CreateInStream(InStr);
        InStr.ReadText(ReadBack);
        f.Close();
        exit(ReadBack);
    end;

    procedure CreateAndLen(Content: Text): Integer
    var
        f: File;
        OutStr: OutStream;
    begin
        f.Create('len.tmp');
        f.CreateOutStream(OutStr);
        OutStr.WriteText(Content);
        exit(f.Len());
    end;
}
