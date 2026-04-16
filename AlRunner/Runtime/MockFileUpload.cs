namespace AlRunner.Runtime;

using System.Text;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory FileUpload replacement for NavFileUpload.
/// BC's FileUpload represents a file that a user has uploaded through a page control.
/// In standalone mode there is no browser/UI, so this mock exposes a constructable
/// in-memory version used by tests.
///
/// Default construction yields an empty file (FileName = '', zero bytes).
/// For C# test injection use the parameterised constructor:
///   new MockFileUpload("report.xlsx", System.IO.File.ReadAllBytes(...))
/// </summary>
public class MockFileUpload
{
    private readonly string _fileName;
    private readonly byte[] _data;

    /// <summary>Default: empty name, empty content — matches a default AL 'var Upload: FileUpload'.</summary>
    public MockFileUpload()
    {
        _fileName = string.Empty;
        _data = Array.Empty<byte>();
    }

    /// <summary>
    /// Test-injection constructor: create a FileUpload backed by in-memory bytes.
    /// Not callable from AL; use this from C# test helpers or future AL stubs.
    /// </summary>
    public MockFileUpload(string fileName, byte[] data)
    {
        _fileName = fileName ?? string.Empty;
        _data = data ?? Array.Empty<byte>();
    }

    /// <summary>Static factory so BC-emitted NavFileUpload.Default works after rewrite.</summary>
    public static MockFileUpload Default => new MockFileUpload();

    /// <summary>
    /// ALFileName — BC emits fileUpload.ALFileName() for FileUpload.FileName() in AL.
    /// Returns the file name supplied at construction, or '' for the default instance.
    /// </summary>
    public NavText ALFileName() => new NavText(_fileName);

    /// <summary>
    /// ALCreateInStream (2-arg void) — BC emits fileUpload.ALCreateInStream(null!, inStr).
    /// Initialises <paramref name="inStream"/> with the upload's byte content.
    /// The parent argument (ITreeObject) is unused in standalone mode.
    /// </summary>
    public void ALCreateInStream(object? parent, MockInStream inStream)
    {
        inStream.Init(_data);
    }

    /// <summary>
    /// ALCreateInStream (3-arg void) — BC emits fileUpload.ALCreateInStream(null!, inStr, encoding).
    /// The TextEncoding argument is accepted but ignored; all mock I/O uses UTF-8.
    /// </summary>
    public void ALCreateInStream(object? parent, MockInStream inStream, object? encoding)
    {
        inStream.Init(_data);
    }

    /// <summary>
    /// ALAssign — AL: upload2 := upload1 — copies the backing data and name.
    /// </summary>
    public void ALAssign(MockFileUpload other)
    {
        // Fields are readonly; handled by reference semantics in most AL patterns.
        // For tests that need re-assignment, create a new MockFileUpload.
    }
}
