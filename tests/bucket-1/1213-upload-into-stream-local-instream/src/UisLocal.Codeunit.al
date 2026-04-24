/// Helper exercising the 2-arg form of UploadIntoStream where the InStream
/// is a LOCAL variable (not a var parameter). This matches the call shape in
/// BBW Item List reported by telemetry (issues #1213/#1214):
///
///     if not UploadIntoStream('Title', FileInStream) then ...
///
/// where `FileInStream` is declared locally inside the procedure. BC's
/// compiler emits this as `ALUploadIntoStream(DataError.TrapError, title,
/// ByRef<MockInStream>, Guid)`. A DataError-typed overload (MockFile.cs) is
/// required so C# overload resolution picks a matching signature instead of
/// trying to bind to the 4-arg `(string, string, ByRef<NavText>, MockInStream)`
/// and raising CS1503 at args 1 (DataError→string) and 4 (Guid→MockInStream).
codeunit 121211 "UIS Local Src"
{
    /// <summary>
    /// Calls UploadIntoStream with a LOCAL InStream variable.
    /// Returns the Boolean result so the test can assert it directly.
    /// </summary>
    procedure CallUploadIntoStreamLocal(): Boolean
    var
        FileInStream: InStream;
    begin
        exit(UploadIntoStream('Upload', FileInStream));
    end;
}
