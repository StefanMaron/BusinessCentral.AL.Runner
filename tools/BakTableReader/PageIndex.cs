// PageIndex — maps a logical SQL Server page address (FileId, PageId) to its
// absolute 8192-byte block number within the .bak stream.
//
// WHY THIS EXISTS (see AL Runner issue #2241)
//   The naive approach -- locate the boot page (always logical 1:9), record
//   its absolute block offset, and add/subtract PageId deltas from there --
//   works for the first few hundred pages of a real BC demo .bak and then
//   silently breaks: the file has at least one place where an MTF backup-set
//   structure is spliced into the otherwise-contiguous 8192-byte-aligned page
//   stream, shifting every page after it by a constant that is NOT the same
//   constant as before the splice. Chasing a NextPage pointer past that point
// with fixed-offset arithmetic lands on the WRONG physical page -- one that
//   still parses as a plausible header (so it doesn't fail loudly) but
//   belongs to an unrelated table.
//
//   The only address that is always correct is the page's OWN self-reported
//   (FileId, PageId) in its header (PageHeader.Self). This class builds a
//   dictionary from that self-reported address to the block's absolute
//   position by scanning the whole file once. Measured cost: ~0.13-0.45s
//   warm, ~0.3-0.4s cold on the machine this was written on (fast NVMe --
//   see the issue for the full cold/warm measurement and its caveats).
namespace BakTableReader;

public sealed class PageIndex
{
    private readonly Dictionary<PagePointer, long> _blockByAddress;

    public int PageCount => _blockByAddress.Count;

    private PageIndex(Dictionary<PagePointer, long> blockByAddress)
    {
        _blockByAddress = blockByAddress;
    }

    public bool TryGetBlock(PagePointer address, out long block) =>
        _blockByAddress.TryGetValue(address, out block);

    public long GetBlock(PagePointer address) =>
        _blockByAddress.TryGetValue(address, out var block)
            ? block
            : throw new KeyNotFoundException($"page {address} not present in the index");

    /// <summary>Scans every 8192-byte-aligned block in <paramref name="stream"/>
    /// once, keeping the LAST block seen for a given self-reported address --
    /// a handful of blocks (observed: ~1.3% on a 894MB W1 demo .bak) collide
    /// because random bytes inside a LOB/TextMix page body happen to satisfy
    /// the page-header heuristic and produce a bogus (FileId, PageId). This
    /// index is only ever used to resolve addresses a real page header
    /// actually pointed at (boot page / NextPage / AU pgfirst), so a rare
    /// false-positive entry sitting unused in the dictionary is harmless.</summary>
    public static PageIndex Build(Stream stream)
    {
        var map = new Dictionary<PagePointer, long>();
        stream.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[PageHeader.PageLength];
        long block = 0;
        // The .bak trailer is not necessarily a whole number of 8192-byte blocks
        // (observed: a 4096-byte remainder on a 894MB W1 demo .bak, presumably
        // an MTF end-of-set marker) -- a short final read is expected, not an
        // error; only whole blocks are ever indexed.
        while (ReadFully(stream, buffer) == buffer.Length)
        {
            if (PageHeader.TryParse(buffer, out var header))
                map[header.Self] = block;
            block++;
        }
        return new PageIndex(map);
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }
}
