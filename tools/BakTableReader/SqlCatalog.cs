// SqlCatalog — walks the SQL Server system catalog (sysallocunits / sysrowsets
// / sysschobjs) far enough to turn a table's SQL object name into the first
// data page of its row storage. Byte offsets ported from OrcaMDF's BaseTables
// schemas (github.com/improvedk/OrcaMDF, GPL-3.0 licensed -- an earlier
// version of this comment said MIT, which was wrong and unverified; see PR
// #2243 for the unresolved licensing question this raises), each RE-VERIFIED
// against real BC demo data for AL Runner #2241 because the underlying SQL
// Server engine has drifted since OrcaMDF was written (~2011): several
// catalog rows here carry one more hidden fixed-length column than OrcaMDF's
// schema documents (e.g. sysschobj: 44 fixed bytes / 12 columns here, not
// OrcaMDF's 40 bytes / 11). The system object ids themselves (5, 7, 34, 41 --
// SystemObject enum in OrcaMDF) are stable and matched real data exactly;
// those small integer ids are facts about SQL Server, not expression.
//
// SCOPE: catalog rows are read here using FixedVarRecord (the uncompressed
// format) -- every system catalog table observed in the #2241 measurement was
// uncompressed. USER table data may or may not be; check
// SysRowset.CompressionLevel before choosing FixedVarRecord vs CompressedRecord
// to decode a table's own rows (see BakTableReaderSpike for the worked example).
using BakTableReader.RecordDecoding;

namespace BakTableReader;

public sealed record SysAllocUnit(long AuId, byte Type, long OwnerId, PagePointer PgFirst, PagePointer PgRoot);

public sealed record SysRowset(long RowsetId, byte OwnerType, int IdMajor, int IdMinor, long RowCount, byte CompressionLevel);

public sealed record SysSchObj(int Id, string Name, string Type);

public sealed class SqlCatalog
{
    private readonly BakFile _file;
    private List<SysAllocUnit>? _sysAllocUnits;
    private List<SysRowset>? _sysRowsets;
    private List<SysSchObj>? _sysSchObjs;

    public SqlCatalog(BakFile file) => _file = file;

    /// <summary>Boot page is always logical (1:9). Returns the FirstSysIndexes
    /// pointer -- the head of the sysallocunits row chain.</summary>
    public PagePointer ReadFirstSysIndexes()
    {
        var page = _file.ReadPage(new PagePointer(1, 9));
        if (!PageHeader.TryParse(page, out var header) || header.Type != PageType.Boot)
            throw new InvalidDataException("logical page (1:9) is not a boot page");
        var slots = PageHeader.ReadSlotArray(page, header.SlotCount);
        var record = FixedVarRecord.Parse(page, slots[0]);
        int pageId = BitConverter.ToInt32(record.FixedLengthData, 512);
        short fileId = BitConverter.ToInt16(record.FixedLengthData, 516);
        return new PagePointer(fileId, pageId);
    }

    public IReadOnlyList<SysAllocUnit> SysAllocUnits => _sysAllocUnits ??= ReadSysAllocUnits().ToList();
    public IReadOnlyList<SysRowset> SysRowsets => _sysRowsets ??= ReadSysRowsets().ToList();
    public IReadOnlyList<SysSchObj> SysSchObjs => _sysSchObjs ??= ReadSysSchObjs().ToList();

    private IEnumerable<SysAllocUnit> ReadSysAllocUnits()
    {
        foreach (var record in WalkFixedVarRows(ReadFirstSysIndexes()))
        {
            var fd = record.FixedLengthData;
            if (fd.Length < 65)
                continue;
            yield return new SysAllocUnit(
                AuId: BitConverter.ToInt64(fd, 0),
                Type: fd[8],
                OwnerId: BitConverter.ToInt64(fd, 9),
                PgFirst: ReadPagePointer6(fd, 23),
                PgRoot: ReadPagePointer6(fd, 29));
        }
    }

    private IEnumerable<SysRowset> ReadSysRowsets()
    {
        // Fixed allocation unit id for sysrowsets itself (OrcaMDF
        // FixedSystemObjectAllocationUnits.sysrowsets); confirmed against real
        // data (its own sysrowsets self-entry: idmajor=5, idminor=1).
        const long sysrowsetsAuId = 327680;
        var au = SysAllocUnits.Single(a => a.AuId == sysrowsetsAuId);
        foreach (var record in WalkFixedVarRows(au.PgFirst))
        {
            var fd = record.FixedLengthData;
            if (fd.Length < 53)
                continue;
            yield return new SysRowset(
                RowsetId: BitConverter.ToInt64(fd, 0),
                OwnerType: fd[8],
                IdMajor: BitConverter.ToInt32(fd, 9),
                IdMinor: BitConverter.ToInt32(fd, 13),
                RowCount: BitConverter.ToInt64(fd, 27),
                CompressionLevel: fd[35]);
        }
    }

    private IEnumerable<SysSchObj> ReadSysSchObjs()
    {
        const int sysschobjsObjectId = 34;
        var rowset = SysRowsets.Single(r => r.IdMajor == sysschobjsObjectId && r.IdMinor == 1);
        var au = SysAllocUnits.Single(a => a.OwnerId == rowset.RowsetId && a.Type == 1);
        foreach (var record in WalkFixedVarRows(au.PgFirst))
        {
            var fd = record.FixedLengthData;
            if (fd.Length < 15 || record.VariableLengthColumns.Count == 0)
                continue;
            int id = BitConverter.ToInt32(fd, 0);
            string name = System.Text.Encoding.Unicode.GetString(record.VariableLengthColumns[0].Data);
            string type = System.Text.Encoding.ASCII.GetString(fd, 13, 2);
            yield return new SysSchObj(id, name, type);
        }
    }

    /// <summary>Resolves a table's rowset (idminor 1 -- the primary/clustered
    /// row storage, not a secondary index) and the allocation unit that holds
    /// its rows.</summary>
    public (SysRowset Rowset, SysAllocUnit AllocUnit) GetTableStorage(int sqlObjectId, int idMinor = 1)
    {
        var rowset = SysRowsets.Single(r => r.IdMajor == sqlObjectId && r.IdMinor == idMinor);
        var au = SysAllocUnits.Single(a => a.OwnerId == rowset.RowsetId && a.Type == 1);
        return (rowset, au);
    }

    /// <summary>Follows the page-header Next-page chain starting at
    /// <paramref name="first"/>, yielding every Primary record found on Data
    /// pages. Ghost/other record types are skipped.</summary>
    public IEnumerable<FixedVarRecord> WalkFixedVarRows(PagePointer first)
    {
        var current = first;
        while (current != PagePointer.Zero)
        {
            var page = _file.ReadPage(current);
            if (!PageHeader.TryParse(page, out var header))
                throw new InvalidDataException($"page {current} has no valid header");
            if (header.Type == PageType.Data)
            {
                foreach (var slot in PageHeader.ReadSlotArray(page, header.SlotCount))
                {
                    var record = FixedVarRecord.Parse(page, slot);
                    if (record.Type == RecordType.Primary)
                        yield return record;
                }
            }
            current = header.Next;
        }
    }

    /// <summary>Same traversal as <see cref="WalkFixedVarRows"/> but for a
    /// ROW/PAGE-compressed rowset (CompressionLevel >= 1).</summary>
    public IEnumerable<CompressedRecord> WalkCompressedRows(PagePointer first)
    {
        var current = first;
        while (current != PagePointer.Zero)
        {
            var page = _file.ReadPage(current);
            if (!PageHeader.TryParse(page, out var header))
                throw new InvalidDataException($"page {current} has no valid header");
            if (header.Type == PageType.Data)
            {
                foreach (var slot in PageHeader.ReadSlotArray(page, header.SlotCount))
                    yield return CompressedRecord.Parse(page, slot);
            }
            current = header.Next;
        }
    }

    private static PagePointer ReadPagePointer6(byte[] buffer, int offset)
    {
        int pageId = BitConverter.ToInt32(buffer, offset);
        short fileId = BitConverter.ToInt16(buffer, offset + 4);
        return new PagePointer(fileId, pageId);
    }
}
