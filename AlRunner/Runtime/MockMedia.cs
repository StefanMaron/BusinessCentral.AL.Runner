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

    /// <summary>BC emits <c>m.ALHasValue(errorLevel)</c> for <c>Media.HasValue()</c>.</summary>
    public bool ALHasValue(DataError errorLevel) => _hasValue;
    public bool ALHasValue() => _hasValue;

    // ── MediaId ──────────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>m.ALMediaId(errorLevel)</c> for <c>Media.MediaId()</c>.</summary>
    public NavGuid ALMediaId(DataError errorLevel) => new NavGuid(_id);
    public NavGuid ALMediaId() => new NavGuid(_id);

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

    // ── FindOrphans ──────────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>NavMedia.ALFindOrphans(errorLevel)</c> for the static <c>Media.FindOrphans()</c>.
    /// Returns an empty list — no orphaned media in a standalone test environment.
    /// </summary>
    public static NavList<NavGuid> ALFindOrphans(DataError errorLevel)
        => NavList<NavGuid>.Default;

    public static NavList<NavGuid> ALFindOrphans()
        => NavList<NavGuid>.Default;

    // ── GetDocumentUrl ────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>NavMedia.ALGetDocumentUrl(errorLevel, mediaId)</c> for <c>Media.GetDocumentUrl()</c>.</summary>
    public static NavText ALGetDocumentUrl(DataError errorLevel, NavGuid mediaId) => NavText.Empty;
    public static NavText ALGetDocumentUrl(NavGuid mediaId) => NavText.Empty;

    // ── ImportWithUrlAccess ───────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>NavMedia.ALImportWithUrlAccess(stream, fileName, duration)</c> for
    /// <c>ImportStreamWithUrlAccess()</c>. BC wraps the return in <c>GuidToNavText</c>,
    /// so the method returns a <c>Guid</c> (not NavText).
    /// </summary>
    public static Guid ALImportWithUrlAccess(DataError errorLevel, MockInStream stream, NavText fileName, int duration) => Guid.Empty;
    public static Guid ALImportWithUrlAccess(DataError errorLevel, MockInStream stream, string fileName, int duration) => Guid.Empty;
    public static Guid ALImportWithUrlAccess(MockInStream stream, NavText fileName, int duration) => Guid.Empty;
    public static Guid ALImportWithUrlAccess(MockInStream stream, string fileName, int duration) => Guid.Empty;

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
