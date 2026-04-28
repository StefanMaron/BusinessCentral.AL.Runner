codeunit 1320408 "File ViewFromStream Bool Src"
{
    /// <summary>
    /// Exercises File.ViewFromStream(InStream, FileName) in a boolean context.
    /// </summary>
    procedure ViewFromStreamInIf(): Boolean
    var
        InStr: InStream;
    begin
        if not File.ViewFromStream(InStr, 'test.txt') then
            exit(false);
        exit(true);
    end;

    /// <summary>
    /// Exercises File.ViewFromStream(InStream, FileName, IsEditable) in a boolean context.
    /// </summary>
    procedure ViewFromStreamEditableInIf(): Boolean
    var
        InStr: InStream;
    begin
        if not File.ViewFromStream(InStr, 'test.txt', true) then
            exit(false);
        exit(true);
    end;
}
