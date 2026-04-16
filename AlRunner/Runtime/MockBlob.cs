namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory BLOB replacement for NavBLOB.
/// Stores raw bytes; supports CreateInStream/CreateOutStream for text round-trip.
/// </summary>
public class MockBlob : NavValue
{
    private byte[] _data = Array.Empty<byte>();

    public bool ALHasValue => _data.Length > 0;

    public int ALLength => _data.Length;

    /// <summary>
    /// ALCreateInStream — 2-arg void overload: (ITreeObject parent, MockInStream inStream)
    /// BC emits: blob.ALCreateInStream(null!, iStr)  (TextEncoding has default)
    /// Initializes the given stream with this BLOB's data.
    /// </summary>
    public void ALCreateInStream(object? parent, MockInStream inStream)
    {
        inStream.Init(_data);
    }

    /// <summary>
    /// ALCreateInStream — 3-arg void overload: (ITreeObject parent, MockInStream inStream, TextEncoding encoding)
    /// BC emits: blob.ALCreateInStream(null!, iStr, TextEncoding::UTF8)
    /// The encoding argument is ignored; all mock I/O uses UTF-8 strings.
    /// </summary>
    public void ALCreateInStream(object? parent, MockInStream inStream, object? encoding)
    {
        inStream.Init(_data);
    }

    /// <summary>
    /// ALCreateInStream — 1-arg overload returning a new MockInStream.
    /// BC emits: var iStr = blob.ALCreateInStream(null!)
    /// </summary>
    public MockInStream ALCreateInStream(object? parent)
    {
        var stream = new MockInStream();
        stream.Init(_data);
        return stream;
    }

    /// <summary>
    /// ALCreateOutStream — 2-arg void overload: (ITreeObject parent, MockOutStream outStream)
    /// BC emits: blob.ALCreateOutStream(null!, oStr)  (TextEncoding has default)
    /// Initializes the given stream; data written to it is flushed back to this BLOB.
    /// </summary>
    public void ALCreateOutStream(object? parent, MockOutStream outStream)
    {
        outStream.Init();
        outStream.OnFlush = d => _data = d;
    }

    /// <summary>
    /// ALCreateOutStream — 3-arg void overload: (ITreeObject parent, MockOutStream outStream, TextEncoding encoding)
    /// BC emits: blob.ALCreateOutStream(null!, oStr, TextEncoding::UTF8)
    /// The encoding argument is ignored; all mock I/O uses UTF-8 strings.
    /// </summary>
    public void ALCreateOutStream(object? parent, MockOutStream outStream, object? encoding)
    {
        outStream.Init();
        outStream.OnFlush = d => _data = d;
    }

    /// <summary>
    /// ALCreateOutStream — 1-arg overload returning a new MockOutStream.
    /// </summary>
    public MockOutStream ALCreateOutStream(object? parent)
    {
        var stream = new MockOutStream();
        stream.Init();
        stream.OnFlush = d => _data = d;
        return stream;
    }

    /// <summary>
    /// ALExport — BC emits <c>blob.ALExport(null!, fileName)</c> for <c>blob.Export(fileName)</c>.
    /// File I/O is out of scope for the runner — stubs return false.
    /// </summary>
    public bool ALExport(object? parent, string fileName) => false;

    /// <summary>
    /// ALImport — BC emits <c>blob.ALImport(null!, fileName)</c> for <c>blob.Import(fileName)</c>.
    /// File I/O is out of scope for the runner — stubs return false.
    /// </summary>
    public bool ALImport(object? parent, string fileName) => false;

    public void ALAssign(MockBlob other)
    {
        _data = (byte[])other._data.Clone();
    }

    public void Clear()
    {
        _data = Array.Empty<byte>();
    }

    // NavValue abstract members
    public override NavNclType NclType => (NavNclType)33; // BLOB type ID in BC
    public override bool IsMutable => true;
    public override object ValueAsObject => _data;
    public override object ClientObject => _data;
    public override bool IsZeroOrEmpty => _data.Length == 0;
    public override int GetBytesSize => _data.Length;

    public override int GetHashCode() => _data.GetHashCode();
    public override bool Equals(NavValue? other) => other is MockBlob b && _data.SequenceEqual(b._data);
}
