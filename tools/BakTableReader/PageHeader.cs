// PageHeader — the 96-byte SQL Server page header, present at the start of every
// 8192-byte page in the demo .bak that ships inside a BC sandbox artifact.
//
// Byte layout ported from OrcaMDF (github.com/improvedk/OrcaMDF, MIT-licensed)
// PageHeader.cs, which documents the same offsets used by DBCC PAGE. See
// AL Runner issue #2241 for how this was cross-checked against real BC data
// (the page's own self-reported PageId/FileId round-tripping against the
// logical address used to read it).
namespace BakTableReader;

public readonly record struct PagePointer(short FileId, int PageId)
{
    public static readonly PagePointer Zero = new(0, 0);
}

/// <summary>The set of page types this reader recognises as a plausible page
/// header. Anything else (including random bytes inside a LOB/TextMix page
/// body that happen to satisfy HeaderVersion==1) is treated as "not a page".</summary>
public enum PageType : byte
{
    Data = 1,
    Index = 2,
    TextMix = 3,
    TextTree = 4,
    Sort = 7,
    Gam = 8,
    Sgam = 9,
    Iam = 10,
    Pfs = 11,
    Boot = 13,
    FileHeader = 15,
    DiffMap = 16,
    MlMap = 17,
}

public readonly struct PageHeader
{
    public const int HeaderLength = 96;
    public const int PageLength = 8192;

    public byte HeaderVersion { get; }
    public PageType Type { get; }
    public short SlotCount { get; }
    /// <summary>Raw m_objId. On modern SQL Server this is NOT a reliable
    /// sys.objects.object_id for user tables (only for the fixed system
    /// tables) -- do not use it to identify a table.</summary>
    public int RawObjectId { get; }
    public PagePointer Self { get; }
    public PagePointer Next { get; }
    public PagePointer Previous { get; }
    public short FreeData { get; }

    private PageHeader(byte headerVersion, PageType type, short slotCount, int rawObjectId,
        PagePointer self, PagePointer next, PagePointer previous, short freeData)
    {
        HeaderVersion = headerVersion;
        Type = type;
        SlotCount = slotCount;
        RawObjectId = rawObjectId;
        Self = self;
        Next = next;
        Previous = previous;
        FreeData = freeData;
    }

    /// <summary>True if the header version and page type look like a real page
    /// header. This is a heuristic (95% of the .bak's 8192-byte-aligned blocks
    /// pass it in practice) -- a small number of false positives come from
    /// LOB page bodies whose random bytes happen to satisfy it. Cross-check
    /// against <see cref="Self"/> when addressing is important.</summary>
    public static bool TryParse(ReadOnlySpan<byte> page, out PageHeader header)
    {
        header = default;
        if (page.Length < HeaderLength)
            return false;

        byte headerVersion = page[0];
        byte rawType = page[1];
        if (headerVersion != 1 || !Enum.IsDefined(typeof(PageType), rawType))
            return false;

        var type = (PageType)rawType;
        int prevPageId = BitConverter.ToInt32(page.Slice(8, 4));
        short prevFileId = BitConverter.ToInt16(page.Slice(12, 2));
        int nextPageId = BitConverter.ToInt32(page.Slice(16, 4));
        short nextFileId = BitConverter.ToInt16(page.Slice(20, 2));
        short slotCount = BitConverter.ToInt16(page.Slice(22, 2));
        int rawObjectId = BitConverter.ToInt32(page.Slice(24, 4));
        short freeData = BitConverter.ToInt16(page.Slice(30, 2));
        int selfPageId = BitConverter.ToInt32(page.Slice(32, 4));
        short selfFileId = BitConverter.ToInt16(page.Slice(36, 2));

        header = new PageHeader(
            headerVersion, type, slotCount, rawObjectId,
            new PagePointer(selfFileId, selfPageId),
            new PagePointer(nextFileId, nextPageId),
            new PagePointer(prevFileId, prevPageId),
            freeData);
        return true;
    }

    /// <summary>Reads the 2-byte, page-relative slot array from the tail of the
    /// page -- SlotCount entries, each pointing at a record's start offset
    /// within the page, stored back-to-front from the very last 2 bytes.</summary>
    public static short[] ReadSlotArray(ReadOnlySpan<byte> page, short slotCount)
    {
        var slots = new short[slotCount];
        for (int i = 0; i < slotCount; i++)
            slots[i] = BitConverter.ToInt16(page.Slice(page.Length - i * 2 - 2, 2));
        return slots;
    }
}
