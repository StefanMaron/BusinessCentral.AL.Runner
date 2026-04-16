namespace AlRunner.Runtime;

using System.Text;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory stub for BC's <c>File</c> / <c>NavFile</c> type.
/// Real BC File I/O requires an OS filesystem and a service-tier session.
/// This mock provides enough behaviour for unit-test code paths:
///   - Static methods (File.Exists, File.Copy, File.Erase, File.Rename, File.IsPathTemporary)
///     are no-ops or return safe defaults.
///   - Instance methods (Create, Open, Close, Read, Write, Seek, Trunc, Len, Pos,
///     Name, TextMode, WriteMode, GetStamp, SetStamp, CreateInStream, CreateOutStream,
///     Download, Upload, View, ViewFromStream, CreateTempFile) are backed by an
///     in-memory byte array so round-trip text I/O works in tests.
///
/// Path/name parameters use <c>string</c> rather than <c>NavText</c> because BC compiles
/// AL string literals to C# <c>string</c> and <c>NavText</c> has an implicit <c>string</c>
/// operator, so both NavText variables and string literals are accepted.
/// </summary>
public class MockFile
{
    private byte[] _data = Array.Empty<byte>();
    private int _pos = 0;
    private bool _textMode = true;
    private bool _writeMode = false;
    private string _name = string.Empty;

    // ── Constructors ─────────────────────────────────────────────────────────

    public MockFile() { }

    /// <summary>1-arg constructor — BC emits <c>new NavFile(this)</c> (scope/parent arg).</summary>
    public MockFile(object? parent) { }

    /// <summary>Static factory for BC-emitted <c>NavFile.Default</c>.</summary>
    public static MockFile Default => new MockFile();

    // ── Static methods ────────────────────────────────────────────────────────

    /// <summary>File.Exists — always false in standalone mode (no real filesystem).</summary>
    public static bool ALExists(string name) => false;

    /// <summary>File.Exists with DataError overload.</summary>
    public static bool ALExists(DataError errorLevel, string name) => false;

    /// <summary>File.Copy — no-op in standalone mode.</summary>
    public static void ALCopy(string fromName, string toName) { }

    public static void ALCopy(DataError errorLevel, string fromName, string toName) { }

    /// <summary>File.Erase — no-op in standalone mode.</summary>
    public static void ALErase(string name) { }

    public static void ALErase(DataError errorLevel, string name) { }

    /// <summary>File.Rename — no-op in standalone mode.</summary>
    public static void ALRename(string oldName, string newName) { }

    public static void ALRename(DataError errorLevel, string oldName, string newName) { }

    /// <summary>File.IsPathTemporary — returns false; no temp FS in standalone mode.</summary>
    public static bool ALIsPathTemporary(string name) => false;

    public static bool ALIsPathTemporary(DataError errorLevel, string name) => false;

    // ── Instance methods ──────────────────────────────────────────────────────

    /// <summary>ALCreate — opens an in-memory buffer for writing.</summary>
    public void ALCreate(object? parent, DataError errorLevel, string name)
    {
        _name = name;
        _data = Array.Empty<byte>();
        _pos = 0;
        _writeMode = true;
    }

    public void ALCreate(object? parent, string name) => ALCreate(parent, DataError.ThrowError, name);

    /// <summary>ALOpen — opens an in-memory buffer for reading.</summary>
    public void ALOpen(object? parent, DataError errorLevel, string name)
    {
        _name = name;
        _pos = 0;
    }

    public void ALOpen(object? parent, string name) => ALOpen(parent, DataError.ThrowError, name);

    /// <summary>ALClose — resets stream position.</summary>
    public void ALClose()
    {
        _pos = 0;
    }

    public void ALClose(object? parent) => ALClose();

    /// <summary>ALWrite — appends UTF-8 encoded text to the in-memory buffer.</summary>
    public void ALWrite(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var newData = new byte[_data.Length + bytes.Length];
        Array.Copy(_data, newData, _data.Length);
        Array.Copy(bytes, 0, newData, _data.Length, bytes.Length);
        _data = newData;
        _pos = _data.Length;
    }

    public void ALWrite(object? parent, string value) => ALWrite(value);

    /// <summary>ALRead — reads one line of UTF-8 text from the in-memory buffer.</summary>
    public void ALRead(ref NavText value)
    {
        int remaining = _data.Length - _pos;
        if (remaining <= 0) { value = NavText.Empty; return; }
        int lineEnd = Array.IndexOf(_data, (byte)'\n', _pos);
        int toRead = lineEnd >= 0 ? lineEnd - _pos + 1 : remaining;
        value = new NavText(Encoding.UTF8.GetString(_data, _pos, toRead).TrimEnd('\n', '\r'));
        _pos += toRead;
    }

    public void ALRead(object? parent, ref NavText value) => ALRead(ref value);

    /// <summary>ALLen — returns the total byte length of the in-memory buffer (BC emits as property).</summary>
    public int ALLen { get => _data.Length; }

    /// <summary>ALPos — returns the current read/write position (BC emits as property).</summary>
    public int ALPos { get => _pos; }

    /// <summary>ALSeek — moves the current position.</summary>
    public void ALSeek(int pos)
    {
        _pos = Math.Max(0, Math.Min(pos, _data.Length));
    }

    public void ALSeek(object? parent, int pos) => ALSeek(pos);

    /// <summary>ALTrunc — truncates the buffer at the current position.</summary>
    public void ALTrunc()
    {
        if (_pos < _data.Length)
        {
            var newData = new byte[_pos];
            Array.Copy(_data, newData, _pos);
            _data = newData;
        }
    }

    public void ALTrunc(object? parent) => ALTrunc();

    /// <summary>ALName — returns the file name set by Create/Open.</summary>
    public NavText ALName() => new NavText(_name);

    public NavText ALName(object? parent) => ALName();

    /// <summary>ALTextMode — get/set text mode flag. BC emits as property access.</summary>
    public bool ALTextMode { get => _textMode; set => _textMode = value; }

    /// <summary>ALWriteMode — get/set write mode flag. BC emits as property access.</summary>
    public bool ALWriteMode { get => _writeMode; set => _writeMode = value; }

    /// <summary>ALGetStamp — returns default DateTime; no real filesystem timestamps.</summary>
    public NavDateTime ALGetStamp() => NavDateTime.Default;

    /// <summary>ALSetStamp — no-op.</summary>
    public void ALSetStamp(NavDateTime stamp) { }

    /// <summary>ALCreateInStream — fills <paramref name="inStream"/> with the current buffer.</summary>
    public void ALCreateInStream(object? parent, MockInStream inStream)
    {
        inStream.Init(_data);
    }

    public void ALCreateInStream(object? parent, MockInStream inStream, object? encoding)
    {
        inStream.Init(_data);
    }

    /// <summary>ALCreateOutStream — prepares an outstream that writes back into this file's buffer.</summary>
    public void ALCreateOutStream(object? parent, MockOutStream outStream)
    {
        outStream.Init();
        outStream.OnFlush = d => _data = d;
    }

    public void ALCreateOutStream(object? parent, MockOutStream outStream, object? encoding)
    {
        outStream.Init();
        outStream.OnFlush = d => _data = d;
    }

    /// <summary>ALCreateTempFile — no-op in standalone mode.</summary>
    public void ALCreateTempFile(object? parent) { }

    /// <summary>ALDownload — no-op; no browser/UI in standalone mode.</summary>
    public void ALDownload(string name) { }

    public void ALDownload(object? parent, string name) { }

    /// <summary>ALUpload — no-op; no browser/UI in standalone mode.</summary>
    public void ALUpload(string name) { }

    public void ALUpload(object? parent, string name) { }

    /// <summary>ALView — no-op; no UI in standalone mode.</summary>
    public void ALView(object? parent) { }

    /// <summary>ALViewFromStream — no-op; no UI in standalone mode.</summary>
    public void ALViewFromStream(object? parent, MockInStream inStream) { }

    /// <summary>ALUploadIntoStream — standalone function; always returns false (no UI).</summary>
    public static bool ALUploadIntoStream(object? scope, DataError errorLevel, string dialogTitle, string fromFolder, string fromFilter, ref NavText fileName, MockInStream inStream)
    {
        fileName = NavText.Empty;
        return false;
    }

    public static bool ALUploadIntoStream(object? scope, DataError errorLevel, string dialogTitle, string fromFolder, string fromFilter, ref NavText fileName, ref MockInStream inStream)
    {
        fileName = NavText.Empty;
        return false;
    }

    // Fallback overloads without scope/DataError in case BC version differs
    public static bool ALUploadIntoStream(string dialogTitle, string fromFolder, string fromFilter, ref NavText fileName, MockInStream inStream)
    {
        fileName = NavText.Empty;
        return false;
    }

    public static bool ALUploadIntoStream(DataError errorLevel, string dialogTitle, string fromFolder, string fromFilter, ref NavText fileName, MockInStream inStream)
    {
        fileName = NavText.Empty;
        return false;
    }

    /// <summary>ALDownloadFromStream — standalone function; no-op (no UI in standalone mode).</summary>
    public static bool ALDownloadFromStream(object? scope, DataError errorLevel, MockInStream inStream, string dialogTitle, string toFolder, string toFilter, ref NavText fileName)
    {
        fileName = NavText.Empty;
        return false;
    }

    // Fallback overloads without scope/DataError
    public static bool ALDownloadFromStream(MockInStream inStream, string dialogTitle, string toFolder, string toFilter, ref NavText fileName)
    {
        fileName = NavText.Empty;
        return false;
    }

    public static bool ALDownloadFromStream(DataError errorLevel, MockInStream inStream, string dialogTitle, string toFolder, string toFilter, ref NavText fileName)
    {
        fileName = NavText.Empty;
        return false;
    }

    /// <summary>ALAssign — copies the backing data and state from another MockFile.</summary>
    public void ALAssign(MockFile other)
    {
        _data = (byte[])other._data.Clone();
        _pos = 0;
        _name = other._name;
        _textMode = other._textMode;
        _writeMode = other._writeMode;
    }
}
