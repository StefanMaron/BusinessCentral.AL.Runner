namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory MediaSet stub replacing NavMediaSet in standalone mode.
/// NavMediaSet's Insert/Remove/Import/Export methods require a BC service tier
/// to access the media catalog. This mock:
///   — Count: returns the number of inserted GUIDs
///   — Insert: adds a GUID to the in-memory set, returns true
///   — Remove: removes a GUID from the in-memory set, returns true if found
///   — Item: returns the GUID at the given 1-based index
///   — MediaId: returns a stable per-instance GUID identifying this set
///   — ImportFile/ImportStream: add a new media GUID, return it
///   — ExportFile: no-op, returns 0 (no blob data in standalone mode)
/// </summary>
public class MockMediaSet : NavValue
{
    private readonly List<Guid> _items = new();
    private readonly Guid _setId = Guid.NewGuid();

    /// <summary>Default instance — BC emits <c>NavMediaSet.Default</c> in some contexts.</summary>
    public static MockMediaSet Default => new MockMediaSet();

    /// <summary>Default constructor.</summary>
    public MockMediaSet() { }

    /// <summary>
    /// 1-arg Guid constructor — BC emits <c>new NavMediaSet(Guid.NewGuid())</c>
    /// when initialising a MediaSet field. The GUID becomes the set identifier.
    /// </summary>
    public MockMediaSet(Guid setId) => _setId = setId;

    // ── NavValue abstract members ─────────────────────────────────────────────

    /// <summary>NclType 57 is a placeholder; AlRunner code never reads NclType on MockMediaSet.</summary>
    public override NavNclType NclType => (NavNclType)57;
    public override bool IsMutable => true;
    public override object ValueAsObject => _setId;
    public override object ClientObject => _setId;
    public override bool IsZeroOrEmpty => _items.Count == 0;
    public override int GetBytesSize => 16;
    public override int GetHashCode() => _setId.GetHashCode();
    public override bool Equals(NavValue? other) => other is MockMediaSet m && _setId == m._setId;

    // ── Count ─────────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>ms.ALCount</c> for <c>MediaSet.Count()</c>.</summary>
    public int ALCount => _items.Count;

    // ── Insert ────────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>ms.ALInsert(errorLevel, mediaId)</c> for <c>MediaSet.Insert(MediaId)</c>.</summary>
    public bool ALInsert(DataError errorLevel, Guid mediaId)
    {
        if (!_items.Contains(mediaId))
            _items.Add(mediaId);
        return true;
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>ms.ALRemove(errorLevel, mediaId)</c> for <c>MediaSet.Remove(MediaId)</c>.</summary>
    public bool ALRemove(DataError errorLevel, Guid mediaId) => _items.Remove(mediaId);

    // ── Item ──────────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>ms.ALItem(index)</c> for <c>MediaSet.Item(Index)</c>. Index is 1-based.</summary>
    public Guid ALItem(int index)
    {
        if (index < 1 || index > _items.Count) return Guid.Empty;
        return _items[index - 1];
    }

    // ── MediaId ───────────────────────────────────────────────────────────────

    /// <summary>BC emits <c>ms.ALMediaId</c> for <c>MediaSet.MediaId()</c>.</summary>
    public Guid ALMediaId => _setId;

    // ── ImportFile ────────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>ms.ALImport(errorLevel, fileName, description)</c> for
    /// <c>MediaSet.ImportFile(FileName, Description)</c>. Returns the new media GUID.
    /// </summary>
    public Guid ALImport(DataError errorLevel, string fileName, string description)
    {
        var id = Guid.NewGuid();
        _items.Add(id);
        return id;
    }

    /// <summary>4-arg overload with explicit MIME type (used by some BC versions).</summary>
    public Guid ALImport(DataError errorLevel, string fileName, string description, string mimeType)
        => ALImport(errorLevel, fileName, description);

    /// <summary>InStream overload — <c>MediaSet.ImportStream(InStream, Description)</c>.</summary>
    public Guid ALImport(DataError errorLevel, MockInStream stream, string description)
    {
        var id = Guid.NewGuid();
        _items.Add(id);
        return id;
    }

    // ── ExportFile ────────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>ms.ALExport(errorLevel, fileName)</c> for
    /// <c>MediaSet.ExportFile(FileName)</c>. Returns 0 — no blob data in standalone mode.
    /// </summary>
    public int ALExport(DataError errorLevel, string fileName) => 0;
}
