namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory FileUpload replacement for NavFileUpload.
/// BC's FileUpload represents a file uploaded through a page control.
/// In standalone mode there is no browser/UI, so this mock exposes an
/// in-memory version.
///
/// Default/1-arg construction yields an empty file (FileName = '', zero bytes).
/// For C# test injection use the parameterised constructor:
///   new MockFileUpload("report.xlsx", System.IO.File.ReadAllBytes(...))
/// </summary>
public class MockFileUpload
{
    private readonly string _fileName;
    private readonly byte[] _data;

    /// <summary>Default: empty name, empty content — matches AL 'var Upload: FileUpload'.</summary>
    public MockFileUpload()
    {
        _fileName = string.Empty;
        _data = Array.Empty<byte>();
    }

    /// <summary>
    /// 1-arg constructor — BC emits <c>new NavFileUpload(this)</c> where <c>this</c>
    /// is the scope object (ITreeObject parent). The parent is unused in standalone mode.
    /// </summary>
    public MockFileUpload(object? parent)
    {
        _fileName = string.Empty;
        _data = Array.Empty<byte>();
    }

    /// <summary>
    /// Test-injection constructor: create a FileUpload backed by in-memory bytes.
    /// Not callable from AL; use this from C# test helpers.
    /// </summary>
    public MockFileUpload(string fileName, byte[] data)
    {
        _fileName = fileName ?? string.Empty;
        _data = data ?? Array.Empty<byte>();
    }

    /// <summary>Static factory so BC-emitted NavFileUpload.Default works after rewrite.</summary>
    public static MockFileUpload Default => new MockFileUpload();

    /// <summary>
    /// ALFileName — BC emits <c>upload.ALFileName</c> (property access) for FileUpload.FileName.
    /// Returns the file name supplied at construction, or '' for the default instance.
    /// </summary>
    public NavText ALFileName => new NavText(_fileName);

    /// <summary>
    /// ALCreateInStream (3-arg void) — BC emits <c>upload.ALCreateInStream(null!, errorLevel, inStr)</c>.
    /// Initialises <paramref name="inStream"/> with the upload's byte content.
    /// The parent and errorLevel arguments are unused in standalone mode.
    /// </summary>
    public void ALCreateInStream(object? parent, DataError errorLevel, MockInStream inStream)
    {
        inStream.Init(_data);
    }

    /// <summary>
    /// ALCreateInStream (4-arg void) — BC emits <c>upload.ALCreateInStream(null!, errorLevel, inStr, encoding)</c>.
    /// The TextEncoding argument is accepted but ignored; all mock I/O uses raw bytes.
    /// </summary>
    public void ALCreateInStream(object? parent, DataError errorLevel, MockInStream inStream, object? encoding)
    {
        inStream.Init(_data);
    }

    /// <summary>
    /// ALAssign — AL: upload2 := upload1 — shallow copy reference (data is immutable after construction).
    /// </summary>
    public void ALAssign(MockFileUpload other)
    {
        // MockFileUpload fields are readonly; the default instance is stateless.
        // If real data injection is needed, construct a new MockFileUpload(fileName, data).
    }
}
