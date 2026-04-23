/// Helper exercising UploadIntoStream stub behaviour.
/// BC 26+ compiles UploadIntoStream(Title, Folder, Filter, FileName, InStream) to a
/// 6-arg C# call.  Newer BC also emits a 4-arg form (no Folder/DataError/Guid) —
/// that path is covered at the C# level in AlRunner.Tests/MockFile4ArgTests.cs
/// (issue #1021).
codeunit 160002 "UIS Src"
{
    /// <summary>
    /// Calls the 5-arg AL form and returns both the Boolean result and the
    /// post-call value of FileName so the test can assert them separately.
    /// </summary>
    procedure CallUploadIntoStream(var FileName: Text; var InStr: InStream): Boolean
    begin
        exit(UploadIntoStream('Upload', '', '*.txt', FileName, InStr));
    end;
}
