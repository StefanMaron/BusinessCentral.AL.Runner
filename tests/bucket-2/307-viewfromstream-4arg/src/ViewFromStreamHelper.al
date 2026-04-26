codeunit 307500 "ViewFromStream Helper"
{
    /// <summary>
    /// Calls ViewFromStream with 3 AL args: InStream, FileName, IsEditable.
    /// BC emits this as the 4-arg C# form:
    ///   ALViewFromStream(parent, inStream, fileName, isEditable)
    /// </summary>
    procedure CallViewFromStream4Arg()
    var
        InStr: InStream;
    begin
        // 3-arg AL form: InStream, FileName, IsEditable
        // BC transpiles to ALViewFromStream(parent, InStr, 'test.txt', true) — 4 C# args
        File.ViewFromStream(InStr, 'test.txt', true);
    end;

    /// <summary>
    /// Calls ViewFromStream with 2 AL args: InStream, FileName.
    /// BC emits this as the 3-arg C# form:
    ///   ALViewFromStream(parent, inStream, fileName)
    /// </summary>
    procedure CallViewFromStream3Arg()
    var
        InStr: InStream;
    begin
        // 2-arg AL form: InStream, FileName
        // BC transpiles to ALViewFromStream(parent, InStr, 'test.txt') — 3 C# args
        File.ViewFromStream(InStr, 'test.txt');
    end;
}
