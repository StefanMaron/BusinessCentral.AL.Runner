// CompressedRecord — the "CD" row format SQL Server uses for ROW- and
// PAGE-compressed tables (sysrowsets.cmprlevel 1 and 2). Ported from OrcaMDF's
// CompressedRecord.cs (github.com/improvedk/OrcaMDF), WITH ONE CORRECTION --
// see below -- found while decoding a real 81-field BC table for AL Runner
// issue #2241.
//
// LICENSE STATUS -- UNRESOLVED, flagging rather than guessing
//   OrcaMDF is GPL-3.0 licensed, not MIT. An earlier version of this file's
//   header said "MIT-licensed"; that was wrong and was never actually
//   verified against OrcaMDF's own License.txt before being written. This
//   file is the one in this directory that tracks OrcaMDF's specific
//   algorithm most closely -- the CD/compressed row format is not documented
//   anywhere else this spike found (unlike the plain page header or the
//   uncompressed FixedVar row format, both independently documented
//   elsewhere), so "ported... WITH ONE CORRECTION" above is an accurate,
//   not a loose, description. Combining GPL-3.0-derived code into this
//   MIT-licensed repository without further action (proper GPL-3.0
//   attribution and compliance, a clean-room rewrite from spec only, or
//   permission from OrcaMDF's author) is a real licensing question, not a
//   missing-notice detail solved by copying in an MIT notice -- see PR #2243
//   for the decision this is waiting on.
//
// THE CORRECTION (short-data-region cluster pointers)
//   Columns are grouped into "clusters" of 30 for the short (<=8-byte) data
//   region. OrcaMDF sizes `shortDataRegionClusterPointers` as `numClusters`,
//   where `numClusters = (numCols - 1) / 30` -- but that value is actually
//   "how many clusters need an explicit length prefix" (total clusters minus
//   one; the last cluster's length is implicit), not the total cluster count.
//   Sizing the pointer array to `numClusters` instead of `numClusters + 1`
//   drops the pointer for the LAST cluster, which is exactly the cluster that
//   holds columns 60-89 on any table with more than 60 non-long columns.
//   "Source Code Setup"'s $ext companion table has 84 columns (3 clusters) and
//   reproduces this: OrcaMDF's own logic throws IndexOutOfRangeException
//   decoding it. This class stores all `numClusters + 1` pointers.
//
// WHAT IS NOT IMPLEMENTED (matches OrcaMDF's own scope, not silently faked)
//   Page-dictionary substitution (CD indicator 0xC) throws NotSupportedException
//   naming the column index, the same way OrcaMDF's own GetPhysicalColumnBytes
//   does -- this reader was never exercised against a page whose dictionary is
//   actually populated (the one real page examined for #2241 had zero
//   dictionary entries), so decoding it would be unverified guesswork.
//   Complex long-data columns (LOB pointers, sparse vectors) are recognised
//   but not resolved for the same reason -- see the #2241 report for what
//   sparse-column resolution on top of this would need.
namespace BakTableReader.RecordDecoding;

public enum ColumnIndicator : byte
{
    Null = 0x0,
    ZeroByte = 0x1,
    OneByte = 0x2,
    TwoByte = 0x3,
    ThreeByte = 0x4,
    FourByte = 0x5,
    FiveByte = 0x6,
    SixByte = 0x7,
    SevenByte = 0x8,
    EightByte = 0x9,
    LongData = 0xA,
    TrueBit = 0xB,
    DictionarySymbol = 0xC,
}

public sealed class CompressedRecord
{
    private static readonly IReadOnlyDictionary<ColumnIndicator, int> LengthByIndicator =
        new Dictionary<ColumnIndicator, int>
        {
            [ColumnIndicator.ZeroByte] = 0,
            [ColumnIndicator.OneByte] = 1,
            [ColumnIndicator.TwoByte] = 2,
            [ColumnIndicator.ThreeByte] = 3,
            [ColumnIndicator.FourByte] = 4,
            [ColumnIndicator.FiveByte] = 5,
            [ColumnIndicator.SixByte] = 6,
            [ColumnIndicator.SevenByte] = 7,
            [ColumnIndicator.EightByte] = 8,
        };

    private readonly byte[] _page;
    private readonly int[] _clusterPointers; // absolute page offsets, one per cluster
    private readonly int[] _longDataPointers;
    private readonly int[] _longDataLengths;

    public bool IsCdFormat { get; }
    public bool HasVersioningInformation { get; }
    public byte RecordType { get; }
    public bool HasLongDataRegion { get; }
    public short NumberOfColumns { get; }
    public IReadOnlyList<ColumnIndicator> Indicators { get; }
    /// <summary>Total bytes this record occupies, relative to <paramref name="start"/>.</summary>
    public int Length { get; }

    private CompressedRecord(byte[] page, bool isCd, bool hasVersioning, byte recordType,
        bool hasLongData, short numCols, IReadOnlyList<ColumnIndicator> indicators,
        int[] clusterPointers, int[] longDataPointers, int[] longDataLengths, int length)
    {
        _page = page;
        IsCdFormat = isCd;
        HasVersioningInformation = hasVersioning;
        RecordType = recordType;
        HasLongDataRegion = hasLongData;
        NumberOfColumns = numCols;
        Indicators = indicators;
        _clusterPointers = clusterPointers;
        _longDataPointers = longDataPointers;
        _longDataLengths = longDataLengths;
        Length = length;
    }

    public static CompressedRecord Parse(byte[] page, int start)
    {
        byte header = page[start];
        bool isCd = (header & 0x1) != 0;
        bool hasVersioning = (header & 0x2) != 0;
        byte recordType = (byte)((header >> 2) & 0x7);
        bool hasLongData = (header & 0x20) != 0;

        int p = 1; // record-relative, right after the header byte

        byte first = page[start + p];
        short numCols;
        if ((first & 0x80) != 0)
        {
            numCols = (short)(BitConverter.ToInt16(page, start + p) & 0x7FFF);
            p += 2;
        }
        else
        {
            numCols = first;
            p += 1;
        }

        var indicators = new ColumnIndicator[numCols];
        for (int i = 0; i < numCols; i++)
        {
            byte b = page[start + p];
            indicators[i] = (ColumnIndicator)(i % 2 == 0 ? b & 0xF : (b & 0xF0) >> 4);
            if (i % 2 == 1)
                p++;
        }
        if (numCols % 2 == 1)
            p++;

        int numLengthPrefixedClusters = numCols > 0 ? (numCols - 1) / 30 : 0;
        int[] clusterPointers;
        if (numLengthPrefixedClusters == 0)
        {
            clusterPointers = new[] { start + p };
            p += ShortRegionLength(indicators, 0, indicators.Length);
        }
        else
        {
            var clusterLengths = new int[numLengthPrefixedClusters];
            for (int i = 0; i < numLengthPrefixedClusters; i++)
                clusterLengths[i] = page[start + p++];

            // total_clusters = numLengthPrefixedClusters + 1 -- see file header.
            clusterPointers = new int[numLengthPrefixedClusters + 1];
            clusterPointers[0] = start + p;
            for (int i = 1; i < numLengthPrefixedClusters; i++)
            {
                int sum = 0;
                for (int j = 0; j < i; j++) sum += clusterLengths[j];
                clusterPointers[i] = clusterPointers[0] + sum;
            }
            int allPrefixedLength = 0;
            foreach (var len in clusterLengths) allPrefixedLength += len;
            int lastClusterStart = p + allPrefixedLength;
            clusterPointers[numLengthPrefixedClusters] = start + lastClusterStart;

            int lastClusterLength = ShortRegionLength(
                indicators, numLengthPrefixedClusters * 30, indicators.Length);
            p = lastClusterStart + lastClusterLength;
        }

        int[] longDataPointers = Array.Empty<int>();
        int[] longDataLengths = Array.Empty<int>();
        if (hasLongData)
        {
            // flags byte (containsTwoByteOffsets / containsComplexColumns) is read
            // by OrcaMDF but never actually consulted -- this reader only
            // supports the two-byte-offset shape, matching every record seen so
            // far in the #2241 measurement.
            p += 1;
            short numEntries = BitConverter.ToInt16(page, start + p);
            p += 2;
            var offsets = new short[numEntries];
            for (int i = 0; i < numEntries; i++)
            {
                offsets[i] = BitConverter.ToInt16(page, start + p);
                p += 2;
            }
            p += numLengthPrefixedClusters; // per-cluster long-data counts, unused here

            longDataPointers = new int[numEntries];
            longDataLengths = new int[numEntries];
            short prevOffset = 0;
            for (int i = 0; i < numEntries; i++)
            {
                longDataPointers[i] = start + p;
                longDataLengths[i] = offsets[i] - prevOffset;
                p += longDataLengths[i];
                prevOffset = offsets[i];
            }
        }

        return new CompressedRecord(page, isCd, hasVersioning, recordType, hasLongData,
            numCols, indicators, clusterPointers, longDataPointers, longDataLengths, p);
    }

    private static int ShortRegionLength(ColumnIndicator[] indicators, int fromInclusive, int toExclusive)
    {
        int total = 0;
        for (int i = fromInclusive; i < toExclusive; i++)
            if (LengthByIndicator.TryGetValue(indicators[i], out var len))
                total += len;
        return total;
    }

    /// <summary>Returns the raw column bytes, or null for SQL NULL.</summary>
    public byte[]? GetColumnBytes(int index)
    {
        var indicator = Indicators[index];
        switch (indicator)
        {
            case ColumnIndicator.Null:
                return null;
            case ColumnIndicator.DictionarySymbol:
                throw new NotSupportedException(
                    $"column {index}: page-dictionary substitution is not implemented (see #2241)");
            case ColumnIndicator.TrueBit:
                return new byte[] { 1 };
            case ColumnIndicator.ZeroByte:
                return Array.Empty<byte>();
            case ColumnIndicator.LongData:
            {
                int longIndex = 0;
                for (int i = 0; i < index; i++)
                    if (Indicators[i] == ColumnIndicator.LongData)
                        longIndex++;
                int length = _longDataLengths[longIndex];
                if ((length & 0x8000) != 0)
                    throw new NotSupportedException(
                        $"column {index}: complex long-data column not implemented (see #2241)");
                return _page.AsSpan(_longDataPointers[longIndex], length).ToArray();
            }
            default:
            {
                int clusterIndex = index / 30;
                int ptr = _clusterPointers[clusterIndex];
                for (int j = clusterIndex * 30; j < index; j++)
                    if (Indicators[j] != ColumnIndicator.LongData)
                        ptr += LengthByIndicator[Indicators[j]];
                int length = LengthByIndicator[indicator];
                return _page.AsSpan(ptr, length).ToArray();
            }
        }
    }
}
