/// Helper exercising the 2-arg AL form of UploadIntoStream.
///
/// Some BC projects call the short form `UploadIntoStream(Title, var InStream)`
/// (telemetry issue #1210). BC's compiler emits that to a C# call whose
/// shape does not match our existing `NavFile.ALUploadIntoStream` overloads,
/// producing CS1503 after the NavInStream -> MockInStream rewrite.
codeunit 121011 "UIS2 Src"
{
    /// <summary>
    /// Calls the 2-arg AL form: UploadIntoStream(Title, var InStream).
    /// Returns the Boolean result so the test can assert it directly.
    /// </summary>
    procedure CallUploadIntoStream2Arg(var InStr: InStream): Boolean
    begin
        exit(UploadIntoStream('Upload', InStr));
    end;
}
