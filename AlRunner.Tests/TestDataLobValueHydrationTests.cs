// TestDataLobValueHydrationTests — the proving tests for issue #2270 (Blob, Media, MediaSet
// and RecordId rebuilt from a backup) and for the DB-NULL half of #2268.
//
// WHAT IS PROVED HERE, AND WHY IT IS HERE
//   Every claim below is about OUR OWN codec — that RecordPatches.ConvertTestDataValue
//   mirrors BC's SQL-cell-to-NavValue conversion for four more of the types #2258 refused.
//   None of them is a statement about what Business Central does with AL source, so
//   .claude/rules/bc-behavior-tests-go-upstream.md does not send them upstream. The
//   end-to-end half — AL reading the hydrated bytes back through an InStream — is
//   tests/test-data-fixture/TestDataLobValues.Codeunit.al, which needs the 900 MB backup CI
//   does not have.
//
//   The inputs are JSON literals of the exact shape MEASURED from reader a431ee4 against
//   sandbox/28.1.49838.50621's BusinessCentral-W1.bak, not invented ones. Where a literal is
//   a real cell from that backup it says which one, so a reader change that altered the wire
//   format would fail here with a readable diff rather than silently reinterpret.
//
// THE ONE THAT CAN SILENTLY GO WRONG
//   A BC Blob column does not hold the field's bytes. It holds BC's CONTAINER: four magic
//   bytes (02 45 7D 5B) followed by a raw Deflate stream, whenever the field is Compressed.
//   A codec that stored the container verbatim would hand AL 12,921 bytes of deflate for
//   Company Information."Picture" where the real value is a 15,225-byte JPEG — and every
//   assertion an AL test could make short of comparing the bytes would still pass.
//   CompressedBlob_IsDecompressedToItsRealContent asserts against that directly.
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataLobValueHydrationTests
{
    /// <summary>A hand-built stand-in for the value metadata half of an NCLMetaField. BC only
    /// ever constructs NCLMetaField itself, from a MetaField and a parent NCLMetaTable, so a
    /// test that insisted on a real one would need a booted engine to assert a conversion
    /// that is pure over (field facts, JSON value).</summary>
    private sealed class ValueMetadata : INavValueMetadata
    {
        internal ValueMetadata(NavNclType nclType, NavType navType, int definedLength = 0)
        {
            NclType = nclType;
            NavType = navType;
            NavDefinedLengthMetadata = definedLength;
        }

        public NavType NavType { get; }
        public NavNclType NclType { get; }
        public int NavDefinedLengthMetadata { get; }
        public NCLOptionMetadata NavOptionMetadata => null!;
    }

    private static readonly ValueMetadata BlobField = new(NavNclType.NavBlob, NavType.BLOB);
    private static readonly ValueMetadata MediaField = new(NavNclType.NavMedia, NavType.Media);
    private static readonly ValueMetadata MediaSetField = new(NavNclType.NavMediaSet, NavType.MediaSet);
    private static readonly ValueMetadata RecordIdField = new(NavNclType.NavRecordId, NavType.RecordID);
    private static readonly ValueMetadata IntegerField = new(NavNclType.NavInteger, NavType.Integer);
    private static readonly ValueMetadata TextField = new(NavNclType.NavText, NavType.Text, 50);
    private static readonly ValueMetadata DurationField = new(NavNclType.NavDuration, NavType.Duration);

    /// <summary>The field facts under test. <paramref name="emptyValue"/> stands in for
    /// NCLMetaField.EmptyValue and <paramref name="compressed"/> for
    /// NCLMetaField.FieldIsCompressed — the two things BC's own reader reads off the field
    /// beyond its value metadata.</summary>
    private static RecordPatches.TestDataFieldFacts Facts(
        INavValueMetadata metadata, NavValue? emptyValue = null, bool compressed = false)
        => new(metadata, () => emptyValue ?? throw new InvalidOperationException(
                   "this test did not expect the codec to ask for the field's empty value"),
               compressed);

    private static NavValue Convert(RecordPatches.TestDataFieldFacts facts, string rawJsonValue)
    {
        using var doc = JsonDocument.Parse(rawJsonValue);
        return RecordPatches.ConvertTestDataValue(
            facts, doc.RootElement.Clone(), 3902, "Retention Policy Setup Line", 10, "Table Filter");
    }

    private static TestDataHydrationRefusal Refusal(RecordPatches.TestDataFieldFacts facts, string rawJsonValue)
        => Assert.Throws<TestDataHydrationRefusal>(() => Convert(facts, rawJsonValue));

    private static string Hex(byte[] bytes) => "\"0x" + System.Convert.ToHexString(bytes) + "\"";

    /// <summary>BC's own container: NavBLOB.BlobMagic, then a RAW deflate stream (no zlib
    /// header), exactly what NavBLOB.GetSqlWritableValue(compressed: true) writes.</summary>
    private static byte[] BcCompressedContainer(byte[] content)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x02, 0x45, 0x7D, 0x5B }, 0, 4);
        using (var deflate = new System.IO.Compression.DeflateStream(
                   ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
            deflate.Write(content, 0, content.Length);
        return ms.ToArray();
    }

    private static byte[] BytesOf(NavBLOB blob)
    {
        var read = new byte[blob.ALLength];
        using var stream = blob.GetStream();
        stream.Position = 0;
        var total = 0;
        while (total < read.Length)
        {
            var n = stream.Read(read, total, read.Length - total);
            if (n <= 0) break;
            total += n;
        }
        Assert.Equal(read.Length, total);
        return read;
    }

    // ------------------------------------------------------------------ Blob --

    [Fact]
    public void CompressedBlob_IsDecompressedToItsRealContent()
    {
        // The exact cell measured in the shipped CRONUS backup: Retention Policy Setup Line,
        // Table ID 405 / Line No. 10000, field 10 "Table Filter". 46 stored bytes; the value
        // is 47 bytes of readable AL filter text.
        const string StoredHex =
            "\"0x02457D5B0B730D0AF6F4F7D330D45408F60F0AF1F473D770CB4CCD4901F2C33D5C835C213C23535B43A0124D0600\"";
        var expected = Encoding.ASCII.GetBytes("VERSION(1) SORTING(Field1) WHERE(Field25=1(1))\0");

        var blob = Assert.IsType<NavBLOB>(Convert(Facts(BlobField, compressed: true), StoredHex));

        Assert.True(blob.IsInMemory);
        Assert.True(blob.ALHasValue);
        Assert.Equal(expected.Length, blob.ALLength);
        Assert.Equal(expected, BytesOf(blob));

        // Stated separately because it IS the defect, not a corollary: storing the container
        // verbatim would give a blob of 46 bytes starting with BC's magic.
        Assert.NotEqual(46, blob.ALLength);
        Assert.Equal((byte)'V', BytesOf(blob)[0]);
    }

    [Fact]
    public void CompressedBlob_RoundTripsAnArbitraryPayload()
    {
        // Deflate on 47 bytes of ASCII could pass by luck; a payload that does not compress
        // (random bytes, longer than any single deflate block) cannot.
        var content = new byte[9_000];
        new Random(20260831).NextBytes(content);

        var blob = Assert.IsType<NavBLOB>(
            Convert(Facts(BlobField, compressed: true), Hex(BcCompressedContainer(content))));

        Assert.Equal(9_000, blob.ALLength);
        Assert.Equal(content, BytesOf(blob));
    }

    [Fact]
    public void UncompressedBlob_KeepsItsStoredBytesVerbatim()
    {
        var content = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 };

        var blob = Assert.IsType<NavBLOB>(Convert(Facts(BlobField, compressed: false), Hex(content)));

        Assert.Equal(6, blob.ALLength);
        Assert.Equal(content, BytesOf(blob));
    }

    [Fact]
    public void AZeroLengthBlobColumn_IsAnEmptyBlob_NotARefusal()
    {
        foreach (var compressed in new[] { true, false })
        {
            var blob = Assert.IsType<NavBLOB>(Convert(Facts(BlobField, compressed: compressed), "\"0x\""));
            Assert.Equal(0, blob.ALLength);
            Assert.False(blob.ALHasValue);
        }
    }

    [Fact]
    public void ACompressedBlobWithoutBcsMagic_RefusesTheTable()
    {
        // BC's own reader returns error 22926086 here rather than reading the bytes as
        // content. Guessing "maybe it is uncompressed after all" would put a deflate stream
        // in the store as if it were the value.
        var ex = Refusal(Facts(BlobField, compressed: true), Hex(new byte[] { 1, 2, 3, 4, 5, 6 }));
        Assert.Contains("Retention Policy Setup Line", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Table Filter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompressedBlobThatIsNotDeflate_RefusesTheTable()
    {
        var notDeflate = new byte[] { 0x02, 0x45, 0x7D, 0x5B, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Throws<TestDataHydrationRefusal>(
            () => Convert(Facts(BlobField, compressed: true), Hex(notDeflate)));
    }

    [Fact]
    public void AnUncompressedBlobThatCarriesBcsContainer_RefusesRatherThanStoringTheContainer()
    {
        // Our own metadata says "not compressed" and the backup says otherwise. One of the
        // two is wrong, and storing the container as content is the silent-wrong outcome.
        var container = BcCompressedContainer(Encoding.ASCII.GetBytes("hello"));
        Assert.Throws<TestDataHydrationRefusal>(
            () => Convert(Facts(BlobField, compressed: false), Hex(container)));
    }

    [Fact]
    public void ABlobThatIsNotTheMeasuredWireShape_RefusesTheTable()
    {
        foreach (var json in new[] { "\"not hex\"", "\"0xABC\"", "\"0xZZ\"", "42", "true" })
            Assert.Throws<TestDataHydrationRefusal>(
                () => Convert(Facts(BlobField, compressed: true), json));
    }

    // ----------------------------------------------------------------- Media --

    [Fact]
    public void Media_HydratesAsTheStoredMediaId()
    {
        // Word Template "EVENT".Template in the shipped CRONUS backup.
        var media = Assert.IsType<NavMedia>(
            Convert(Facts(MediaField), "\"57C8E273-1769-4173-AAED-0A56E3ADCB8D\""));

        // ToGuid(), NOT ALMediaId. `NavMediaValueBase::get_ALMediaId/0` is one of the members
        // NclCecilRewrite replaces, and its replacement SYNTHESISES an id when the container
        // Guid is empty — so reading the stored value back through it would be reading the
        // runner's media shim, not this codec's output, and would answer differently depending
        // on whether the Ncl rewrite happens to be installed in the process. ToGuid() is
        // NavMediaValueBase's own unrewritten `Key.Value`.
        Assert.Equal(Guid.Parse("57C8E273-1769-4173-AAED-0A56E3ADCB8D"), media.ToGuid());
        Assert.False(media.IsZeroOrEmpty);

        // BC's row read passes parentTableId -1 and NavRecord.GetFieldValue overwrites it
        // through SetOwnerRecordInformation, so the stored one is never read. Asserted so a
        // future change to it is a deliberate one.
        Assert.Equal(-1, media.ParentId);
    }

    [Fact]
    public void MediaSet_HydratesAsTheStoredSetId()
    {
        // Item Variant."Picture" in the shipped CRONUS backup.
        var set = Assert.IsType<NavMediaSet>(
            Convert(Facts(MediaSetField), "\"EAAD9A16-3132-4C9C-8206-393598E9F1F0\""));

        Assert.Equal(Guid.Parse("EAAD9A16-3132-4C9C-8206-393598E9F1F0"), set.ToGuid());
        Assert.False(set.IsZeroOrEmpty);
        Assert.Equal(-1, set.ParentId);
    }

    [Fact]
    public void AnAllZeroMediaId_IsTheBlankMedia_NotARefusal()
    {
        var media = Assert.IsType<NavMedia>(
            Convert(Facts(MediaField), "\"00000000-0000-0000-0000-000000000000\""));
        Assert.Equal(Guid.Empty, media.ToGuid());
        Assert.True(media.IsZeroOrEmpty);
    }

    [Fact]
    public void AMediaCellThatIsNotAGuid_RefusesTheTable()
    {
        foreach (var facts in new[] { Facts(MediaField), Facts(MediaSetField) })
        foreach (var json in new[] { "\"not-a-guid\"", "\"0xDEAD\"", "17", "true" })
            Assert.Throws<TestDataHydrationRefusal>(() => Convert(facts, json));
    }

    // -------------------------------------------------------------- RecordId --

    [Fact]
    public void RecordId_HydratesTheTableAndKeyItStores()
    {
        // BC's layout: int32 table no, then (uint16 NavType, value)* terminated by uint16 0.
        // NavType.Integer is 34560 (0x8700). Table 18 "Customer", integer key 10000.
        var bytes = new byte[] { 18, 0, 0, 0, 0x00, 0x87, 0x10, 0x27, 0x00, 0x00, 0x00, 0x00 };

        var id = Assert.IsType<NavRecordId>(Convert(Facts(RecordIdField), Hex(bytes)));

        Assert.Equal(18, id.TableNo);
        Assert.Equal(1, id.FieldCount);
        Assert.False(id.IsZeroOrEmpty);
    }

    [Fact]
    public void AnAllZeroRecordId_IsTheBlankRecordId_NotARefusal()
    {
        // The exact cell measured in the shipped CRONUS backup: Bank Account."Bank Stmt.
        // Service Record ID" and Incoming Document."Related Record ID" both hold six zero
        // bytes. BC reads them into a 448-byte buffer, which is what makes the short cell
        // legal rather than a decoding error.
        var id = Assert.IsType<NavRecordId>(Convert(Facts(RecordIdField), "\"0x000000000000\""));

        Assert.Equal(0, id.TableNo);
        Assert.Equal(0, id.FieldCount);
        Assert.True(id.IsZeroOrEmpty);
    }

    [Fact]
    public void AShortRecordIdCell_IsPaddedTheWayBcPadsIt_NotRefused()
    {
        // Deliberately NOT a refusal, and the reason is the 448-byte buffer. BC does
        // `reader.GetBytes(i, 0, new byte[448], 0, 448)`, so SqlDataReader copies whatever is
        // stored and leaves the rest zero — a cell shorter than 448 bytes is the NORMAL case,
        // not a truncated one, because NavRecordId only ever writes the bytes it uses. Both
        // RecordId columns in the shipped CRONUS backup are six bytes.
        foreach (var json in new[] { "\"0x\"", "\"0x0000\"", "\"0x0000000000\"" })
        {
            var id = Assert.IsType<NavRecordId>(Convert(Facts(RecordIdField), json));
            Assert.Equal(0, id.TableNo);
            Assert.True(id.IsZeroOrEmpty);
        }
    }

    [Fact]
    public void ARecordIdCellBcCouldNotHaveStored_RefusesTheTable()
    {
        // Longer than the 448 bytes BC reads: the reader decoded something BC's own column
        // cannot hold, so the decode is wrong and a truncated RecordId would be a plausible
        // wrong answer.
        var tooLong = "\"0x" + new string('0', (NavRecordId.MaxByteSize + 1) * 2) + "\"";
        Assert.Throws<TestDataHydrationRefusal>(() => Convert(Facts(RecordIdField), tooLong));

        // Not the wire shape at all. (A real JSON null is a different branch — see
        // ANullInAnyOtherColumn_IsTheFieldsEmptyValue.)
        foreach (var json in new[] { "\"nope\"", "\"0xZZZZ\"", "\"0x000\"", "42" })
            Assert.Throws<TestDataHydrationRefusal>(() => Convert(Facts(RecordIdField), json));
    }

    // ---------------------------------------------------------- the DB NULL --

    [Fact]
    public void ANullBlobColumn_IsAnEmptyBlob_NotARefusal()
    {
        // BC: `return (field.NclType == NavNclType.NavBlob) ? new NavBLOB(0) : field.EmptyValue;`
        // The Blob arm does NOT go through EmptyValue, which is why this facts object supplies
        // none — if the codec reached for one, the lambda above would throw and say so.
        var blob = Assert.IsType<NavBLOB>(Convert(Facts(BlobField, compressed: true), "null"));

        Assert.Equal(0, blob.ALLength);
        Assert.False(blob.ALHasValue);
        Assert.False(blob.IsInMemory);
    }

    [Fact]
    public void ANullInAnyOtherColumn_IsTheFieldsEmptyValue()
    {
        // The other arm of the same line. Asserted with Assert.Same so it cannot pass by
        // producing an equal-looking value from somewhere else.
        var emptyInteger = NavInteger.Create(0);
        Assert.Same(emptyInteger, Convert(Facts(IntegerField, emptyValue: emptyInteger), "null"));

        var emptyText = NavText.Default(50);
        Assert.Same(emptyText, Convert(Facts(TextField, emptyValue: emptyText), "null"));

        var emptyMedia = NavMedia.Default;
        Assert.Same(emptyMedia, Convert(Facts(MediaField, emptyValue: emptyMedia), "null"));

        var emptyRecordId = NavRecordId.Default;
        Assert.Same(emptyRecordId, Convert(Facts(RecordIdField, emptyValue: emptyRecordId), "null"));
    }

    // ------------------------------------- the install-baseline disk codec --

    [Fact]
    public void AHydratedMediaSurvivesTheInstallBaselineDiskCodec()
    {
        // Not an incidental claim. RecordPatches.InstallBaselineDisk had NO encoding for
        // Media or MediaSet, and ONE unencodable value makes the whole snapshot unpersistable
        // — so hydrating Customer/Vendor/Item Variant with their Image/Picture would have cost
        // every --test-data run its disk baseline (a ~4-minute rehydration instead of ~8s),
        // announced by nothing but a DiskLog line. This goes through the codec's own two
        // halves rather than re-deriving them here.
        var media = new NavMedia(Guid.Parse("57C8E273-1769-4173-AAED-0A56E3ADCB8D"), parentId: -1);
        var restored = Assert.IsType<NavMedia>(RecordPatches.DecodeInstallBaselineMedia(
            NavNclType.NavMedia, RecordPatches.EncodeInstallBaselineMedia(media)));
        Assert.Equal(media.ToGuid(), restored.ToGuid());
        Assert.NotEqual(Guid.Empty, restored.ToGuid());

        var set = new NavMediaSet(Guid.Parse("EAAD9A16-3132-4C9C-8206-393598E9F1F0"), parentId: -1);
        var restoredSet = Assert.IsType<NavMediaSet>(RecordPatches.DecodeInstallBaselineMedia(
            NavNclType.NavMediaSet, RecordPatches.EncodeInstallBaselineMedia(set)));
        Assert.Equal(set.ToGuid(), restoredSet.ToGuid());

        // A Media must not come back as a MediaSet or vice versa — they are different AL
        // types on different fields, and the kind byte alone does not tell them apart.
        Assert.IsType<NavMediaSet>(RecordPatches.DecodeInstallBaselineMedia(
            NavNclType.NavMediaSet, RecordPatches.EncodeInstallBaselineMedia(media)));
        Assert.Throws<InvalidDataException>(() => RecordPatches.DecodeInstallBaselineMedia(
            NavNclType.NavGuid, RecordPatches.EncodeInstallBaselineMedia(media)));
    }

    // ------------------------------------------------ refusal still works --

    // -------------------------------------------------------------- Duration --

    [Fact]
    public void Duration_HydratesAsThatManyMilliseconds()
    {
        // Job Queue Entry."Job Timeout" in the shipped CRONUS backup: 43,200,000 ms = 12h.
        var duration = Assert.IsType<NavDuration>(Convert(Facts(DurationField), "43200000"));
        Assert.Equal(43_200_000L, duration.Value);
        Assert.False(duration.IsZeroOrEmpty);

        Assert.True(Assert.IsType<NavDuration>(Convert(Facts(DurationField), "0")).IsZeroOrEmpty);

        // BC's Duration is signed; a negative one is a real AL value, not a decoding error.
        Assert.Equal(-1_500L, Assert.IsType<NavDuration>(Convert(Facts(DurationField), "-1500")).Value);
    }

    [Fact]
    public void ADurationCellThatIsNotWholeMilliseconds_RefusesTheTable()
    {
        foreach (var json in new[] { "\"43200000\"", "1.5", "true", "\"0x0A\"" })
            Assert.Throws<TestDataHydrationRefusal>(() => Convert(Facts(DurationField), json));
    }

    // ------------------------------------------------ refusal still works --

    [Fact]
    public void TypesThisBuildStillCannotRebuild_KeepRefusing()
    {
        // Five more reasons to refuse were removed; the ABILITY to refuse was not. TableFilter
        // has a case in BC's reader too — 504 raw bytes — but no CRONUS table stores one, so
        // the shape the backup reader emits for it has never been measured here, and this
        // codec does not invent one. #2271 tracks it.
        var ex = Refusal(
            Facts(new ValueMetadata(NavNclType.NavTableFilter, NavType.TableFilter)), "\"0xDEADBEEF\"");
        Assert.Contains(NavNclType.NavTableFilter.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("Retention Policy Setup Line", ex.Message, StringComparison.Ordinal);
    }
}
