namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Minimal file-dialog replacement. Standalone mode has no client picker, so
/// upload requests fail closed and leave the target stream empty.
/// </summary>
public static class MockFile
{
    // --- ALUploadIntoStream overloads ---
    public static bool ALUploadIntoStream(DataError errorLevel, string filter, ByRef<MockInStream> inStream)
    {
        if (inStream.Value != null)
            inStream.Value.Clear();
        return false;
    }

    public static bool ALUploadIntoStream(DataError errorLevel, string filter, ByRef<MockInStream> inStream, System.Guid uploadId)
    {
        if (inStream.Value != null)
            inStream.Value.Clear();
        return false;
    }

    public static bool ALUploadIntoStream(DataError errorLevel, string dialogTitle, string fromFolder, string filter, ByRef<NavText> fileName, ByRef<MockInStream> inStream, System.Guid uploadId)
    {
        if (inStream.Value != null)
            inStream.Value.Clear();
        return false;
    }

    public static bool ALUploadIntoStream(DataError errorLevel, string dialogTitle, string fromFolder, string filter, ByRef<NavOemText> fileName, ByRef<MockInStream> inStream, System.Guid uploadId)
    {
        if (inStream.Value != null)
            inStream.Value.Clear();
        return false;
    }

    // --- ALDownloadFromStream overloads ---
    public static bool ALDownloadFromStream(DataError errorLevel, MockInStream inStream, string dialogTitle, string fromFolder, string filter, ByRef<NavText> fileName, System.Guid downloadId)
    {
        return false;
    }

    public static bool ALDownloadFromStream(DataError errorLevel, MockInStream inStream, string dialogTitle, string fromFolder, string filter, ByRef<NavOemText> fileName, System.Guid downloadId)
    {
        return false;
    }

    public static bool ALDownloadFromStream(DataError errorLevel, MockInStream inStream, string dialogTitle, string fromFolder, string filter, ByRef<NavText> fileName)
    {
        return false;
    }

    public static bool ALDownloadFromStream(DataError errorLevel, MockInStream inStream, string dialogTitle, ByRef<NavText> fileName)
    {
        return false;
    }
}
