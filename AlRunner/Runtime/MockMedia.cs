namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory Media stub replacing NavMedia in standalone mode.
/// NavMedia's Import/Export methods require a BC service tier to access
/// blob storage and the media catalog. This mock:
///   — ImportFile/ImportStream: set a HasValue flag and return true
///   — ExportFile/ExportStream: no-op, return false (no data stored)
///   — MediaId: returns a stable per-instance GUID
///   — FindOrphans: static stub returning an empty list
/// </summary>
public class MockMedia : NavValue
{
    private bool _hasValue;
    private readonly Guid _id = Guid.NewGuid();

    /// <summary>Default instance — BC emits <c>NavMedia.Default</c> in some contexts.</summary>
    public static MockMedia Default => new MockMedia();

    /// <summary>Default constructor — for MockMedia.Default and direct instantiation.</summary>
    public MockMedia() { }

    /// <summary>
    /// 1-arg constructor — BC emits <c>new NavMedia(this)</c> where <c>this</c>
    /// is the scope object (ITreeObject parent). Unused in standalone mode.
    /// </summary>
    public MockMedia(object? parent) { }

    // ── HasValue ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>m.ALHasValue</c> (property access) for <c>Media.HasValue()</c>.
    /// The legacy method overloads are kept for any older-generated code paths.
    /// </summary>
    public bool ALHasValue => _hasValue;

    // ── MediaId ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>m.ALMediaId</c> (property access, no parentheses) for
    /// <c>Media.MediaId()</c>. Must be a property — a method overload would
    /// produce CS1503 / CS0428 "method group" errors when used as an argument.
    /// </summary>
    public Guid ALMediaId => _id;

    // ── ALImport (BC-emitted name for Media.ImportFile) ───────────────────────────

    /// <summary>
    /// BC emits <c>m.ALImport(errorLevel, fileName, description)</c>
    /// for <c>Media.ImportFile(FileName, Description)</c>.
    /// Returns a stable per-instance GUID (the media ID).
    /// </summary>
    public Guid ALImport(DataError errorLevel, NavText fileName, NavText description)
    {
        _hasValue = true;
        return _id;
    }

    public Guid ALImport(DataError errorLevel, string fileName, string description)
    {
        _hasValue = true;
        return _id;
    }

    public Guid ALImport(DataError errorLevel, NavText fileName, NavText description, NavText mimeType)
    {
        _hasValue = true;
        return _id;
    }

    public Guid ALImport(DataError errorLevel, string fileName, string description, string mimeType)
    {
        _hasValue = true;
        return _id;
    }

    // BC emits ALImport(errorLevel, inStream, description) for Media.ImportStream(InStream, Text)
    public Guid ALImport(DataError errorLevel, MockInStream stream, NavText description)
    {
        _hasValue = true;
        return _id;
    }

    public Guid ALImport(DataError errorLevel, MockInStream stream, string description)
    {
        _hasValue = true;
        return _id;
    }

    public Guid ALImport(DataError errorLevel, MockInStream stream, NavText description, NavText mimeType)
    {
        _hasValue = true;
        return _id;
    }

    public Guid ALImport(DataError errorLevel, MockInStream stream, string description, string mimeType)
    {
        _hasValue = true;
        return _id;
    }

    /// <summary>
    /// BC emits <c>ALImport(errorLevel, stream, fileName, mimeType, description)</c> for
    /// <c>Media.ImportStream(InStream, Text, Text, Text)</c> — the 4-arg AL form.
    /// Sets HasValue to true and returns the stable media GUID.
    /// </summary>
    public Guid ALImport(DataError errorLevel, MockInStream stream, NavText fileName, NavText mimeType, NavText description)
    {
        _hasValue = true;
        return _id;
    }

    public Guid ALImport(DataError errorLevel, MockInStream stream, string fileName, string mimeType, string description)
    {
        _hasValue = true;
        return _id;
    }

    public Guid ALImport(DataError errorLevel, MockInStream stream, NavText fileName, NavText mimeType, string description)
    {
        _hasValue = true;
        return _id;
    }

    public Guid ALImport(DataError errorLevel, MockInStream stream, string fileName, string mimeType, NavText description)
    {
        _hasValue = true;
        return _id;
    }

    // ── ALExport (BC-emitted for Media.ExportFile and Media.ExportStream) ────────

    /// <summary>
    /// BC emits <c>m.ALExport(errorLevel, fileName)</c> for <c>Media.ExportFile(FileName)</c>.
    /// Returns false — no blob data in standalone mode.
    /// </summary>
    public bool ALExport(DataError errorLevel, NavText fileName) => false;
    public bool ALExport(DataError errorLevel, string fileName) => false;

    // BC emits ALExport(errorLevel, outStream) for Media.ExportStream(OutStream)
    public bool ALExport(DataError errorLevel, MockOutStream stream) => false;
    public bool ALExport(MockOutStream stream) => false;

    // ── ImportFile ───────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>m.ALImportFile(errorLevel, fileName)</c> for <c>Media.ImportFile()</c>.</summary>
    public bool ALImportFile(DataError errorLevel, NavText fileName)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportFile(DataError errorLevel, string fileName)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportFile(NavText fileName)
    {
        _hasValue = true;
        return true;
    }

    // BC 16.2 signature: ImportFile(FileName, Description [, MimeType])
    public bool ALImportFile(DataError errorLevel, NavText fileName, NavText description)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportFile(DataError errorLevel, string fileName, string description)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportFile(DataError errorLevel, NavText fileName, NavText description, NavText mimeType)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportFile(DataError errorLevel, string fileName, string description, string mimeType)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportFile(NavText fileName, NavText description)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportFile(NavText fileName, NavText description, NavText mimeType)
    {
        _hasValue = true;
        return true;
    }

    // ── ImportStream ─────────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>m.ALImportStream(errorLevel, inStream, fileName)</c> for
    /// <c>Media.ImportStream(InStream, Text)</c>.
    /// Two overloads per runtime-api.json (with and without MIME type).
    /// </summary>
    public bool ALImportStream(DataError errorLevel, MockInStream stream, NavText fileName)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportStream(DataError errorLevel, MockInStream stream, string fileName)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportStream(DataError errorLevel, MockInStream stream, NavText fileName, NavText mimeType)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportStream(DataError errorLevel, MockInStream stream, string fileName, string mimeType)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportStream(MockInStream stream, NavText fileName)
    {
        _hasValue = true;
        return true;
    }

    public bool ALImportStream(MockInStream stream, string fileName)
    {
        _hasValue = true;
        return true;
    }

    // ── ExportFile ───────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>m.ALExportFile(errorLevel, fileName)</c> for <c>Media.ExportFile()</c>.</summary>
    public bool ALExportFile(DataError errorLevel, NavText fileName) => false;
    public bool ALExportFile(DataError errorLevel, string fileName) => false;
    public bool ALExportFile(NavText fileName) => false;

    // ── ExportStream ─────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>m.ALExportStream(errorLevel, outStream)</c> for <c>Media.ExportStream()</c>.</summary>
    public bool ALExportStream(DataError errorLevel, MockOutStream stream) => false;
    public bool ALExportStream(MockOutStream stream) => false;

    // ── GetDocumentUrl ───────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>NavMedia.ALGetDocumentUrl(mediaId)</c> for the global
    /// <c>GetDocumentUrl(MediaId)</c> built-in. No BC Media service in standalone mode;
    /// returns empty string.
    /// </summary>
    public static string ALGetDocumentUrl(object? mediaId) => "";
    public static string ALGetDocumentUrl(DataError errorLevel, object? mediaId) => "";

    // ── ImportStreamWithUrlAccess ─────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>NavMedia.ALImportWithUrlAccess(stream, filename, duration)</c> for
    /// the global <c>ImportStreamWithUrlAccess(InStream, Text, Integer)</c> built-in.
    /// No BC Media service in standalone mode; returns empty Guid.
    /// </summary>
    public static Guid ALImportWithUrlAccess(MockInStream? stream, string filename, int duration) => Guid.Empty;
    public static Guid ALImportWithUrlAccess(DataError errorLevel, MockInStream? stream, string filename, int duration) => Guid.Empty;
    public static Guid ALImportWithUrlAccess(MockInStream? stream, NavText filename, int duration) => Guid.Empty;
    public static Guid ALImportWithUrlAccess(DataError errorLevel, MockInStream? stream, NavText filename, int duration) => Guid.Empty;

    // ── FindOrphans ──────────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>NavMedia.ALFindOrphans(errorLevel)</c> for the static <c>Media.FindOrphans()</c>.
    /// Returns an empty list — no orphaned media in a standalone test environment.
    /// AL <c>List of [Guid]</c> compiles to <c>NavList&lt;System.Guid&gt;</c>.
    /// </summary>
    public static NavList<Guid> ALFindOrphans(DataError errorLevel)
        => NavList<Guid>.Default;

    public static NavList<Guid> ALFindOrphans()
        => NavList<Guid>.Default;

    // ── GetDocumentUrl ────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>NavMedia.ALGetDocumentUrl(errorLevel, mediaId)</c> for <c>Media.GetDocumentUrl()</c>.</summary>
    public static NavText ALGetDocumentUrl(DataError errorLevel, NavGuid mediaId) => NavText.Empty;
    public static NavText ALGetDocumentUrl(NavGuid mediaId) => NavText.Empty;

    // ── NavValue abstract members ────────────────────────────────────────────────

    /// <summary>NclType 56 is a placeholder; AlRunner code never reads NclType on MockMedia.</summary>
    public override NavNclType NclType => (NavNclType)56;
    public override bool IsMutable => true;
    public override object ValueAsObject => _id;
    public override object ClientObject => _id;
    public override bool IsZeroOrEmpty => !_hasValue;
    public override int GetBytesSize => 16;

    public override int GetHashCode() => _id.GetHashCode();
    public override bool Equals(NavValue? other) => other is MockMedia m && _id == m._id;
}
