using AlRunner.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for the 4-argument C# overload of MockFile.ALUploadIntoStream.
///
/// The 4-arg AL form UploadIntoStream(DialogTitle, Filter, FileName, InStream)
/// (without the optional 'fromFolder' parameter) is emitted by newer BC versions
/// as a 4-arg C# call: ALUploadIntoStream(title, filter, ref fileName, ref inStream).
/// Without the matching 4-param C# overload, the runner fails with CS1501.
///
/// Issue: #1021
/// </summary>
public class MockFile4ArgTests
{
    /// <summary>
    /// Positive: 4-arg ALUploadIntoStream compiles and returns false in standalone mode.
    /// This is a no-op stub test — the entire claim is "this overload exists and does not crash."
    /// </summary>
    [Fact]
    public void ALUploadIntoStream_4Arg_ReturnsFalse()
    {
        var fileName = NavText.Empty;
        var byRefFileName = new ByRef<NavText>(() => fileName, v => fileName = v);
        var inStream = new MockInStream();

        bool result = MockFile.ALUploadIntoStream(
            "Upload",
            "*.txt",
            byRefFileName,
            inStream);

        Assert.False(result, "4-arg ALUploadIntoStream must return false in standalone mode");
    }

    /// <summary>
    /// Positive: 4-arg ALUploadIntoStream clears FileName to empty (stub behaviour).
    /// This proves the stub is not a no-op that ignores its arguments — it actively
    /// clears FileName, which is the correct behaviour for a cancelled/stubbed upload.
    /// </summary>
    [Fact]
    public void ALUploadIntoStream_4Arg_ClearsFileName()
    {
        var fileName = new NavText("preset-value");
        var byRefFileName = new ByRef<NavText>(() => fileName, v => fileName = v);
        var inStream = new MockInStream();

        MockFile.ALUploadIntoStream(
            "Upload",
            "*.txt",
            byRefFileName,
            inStream);

        Assert.Equal(NavText.Empty, fileName);
    }
}
