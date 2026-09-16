using System;
using System.Linq;
using System.Text.Json;
using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4123: the backup's `timestamp` (SQL rowversion) column reaches field 0.
///
/// Two properties, and the SECOND is what makes hydrating safe rather than merely present.
/// A value alone would be a new wrong answer: the runner stamps later writes from a
/// process-wide counter starting at 1 (RowVersionPatches), so a restored 261,652 would make
/// every row a test writes afterwards sort BEFORE every restored row. Real SQL has one
/// monotonic sequence per database; two interleaved wrongly is worse than an absent value,
/// which the `--test-data` summary at least reported honestly.
/// </summary>
public class TestDataRowVersionHydrationTests
{
    // The issue's own example: No. Series A-BLK in BusinessCentral-W1.bak.
    private const string Hex = "0x000000000003FE14";
    private const long Expected = 261_652;

    [Fact]
    public void ParseRows_KeepsTheTimestampColumn_OnItsAlFieldName()
    {
        const string json = """
            [{"Code":"A-BLK","Description":"Blanket Sales Order","timestamp":"0x000000000003FE14"}]
            """;

        var rows = TestDataProvisioner.ParseRows(json);

        var row = Assert.Single(rows);
        Assert.True(row.ContainsKey(RecordPatches.TestDataRowVersionFieldName),
            "the rowversion column must reach field 0's AL field name; it was dropped (#4123). "
            + "Keys present: " + string.Join(", ", row.Keys));
        Assert.Equal(Hex, row[RecordPatches.TestDataRowVersionFieldName].GetString());
    }

    [Theory]
    // Big-endian, per NavSqlCommand.CreateNavValueFromReader -> TimestampToInt64.
    [InlineData("0x000000000003FE14", 261_652L)]
    [InlineData("0x0000000000000001", 1L)]
    [InlineData("0x00000000000003E8", 1000L)]
    public void DecodeRowVersion_ReadsBigEndian(string hex, long expected)
    {
        Assert.Equal(expected, RecordPatches.DecodeTestDataRowVersion(hex));
    }

    [Fact]
    public void DecodeRowVersion_IsNotLittleEndian()
    {
        // The trap this pins: BitConverter.ToInt64 on x86 gives 1,512,649,823,377,948,672 for
        // the same bytes — wrong by ~5.8 trillion, yet still monotonic WITHIN one restore, so
        // the wrong decode is not self-revealing. It only shows against a value from elsewhere.
        Assert.NotEqual(1_512_649_823_377_948_672L, RecordPatches.DecodeTestDataRowVersion(Hex));
        Assert.Equal(Expected, RecordPatches.DecodeTestDataRowVersion(Hex));
    }

    [Fact]
    public void TheHexShape_WouldBeRefusedByTheOrdinaryBigIntegerBranch()
    {
        // Why the column needs its OWN branch rather than merely not being dropped. Field 0 is
        // NavBigInteger, and that branch parses with long.TryParse(NumberStyles.Integer), which
        // refuses "0x…". So keeping the column without decoding it turns every hydration of a
        // table carrying a rowversion into a hard TestDataHydrationRefusal.
        //
        // This is the direction CI could not have caught: the end-to-end fixture that would
        // have failed (tests/test-data-fixture) needs a ~1 GB backup and is deliberately not
        // run by CI, so the refusal would have shipped green.
        Assert.False(long.TryParse(Hex, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out _));

        // …and the dedicated decoder is what makes it work.
        Assert.Equal(Expected, RecordPatches.DecodeTestDataRowVersion(Hex));
    }

    [Fact]
    public void DecodeRowVersion_RefusesAShapeItCannotDecode()
    {
        // A guard that could not measure must refuse, never return a plausible number: a silent
        // 0 here would make every restored row sort first and look like a working hydration.
        Assert.Throws<FormatException>(() => RecordPatches.DecodeTestDataRowVersion("0xZZ"));
        Assert.Throws<FormatException>(
            () => RecordPatches.DecodeTestDataRowVersion("0x000000000000000000"));
        Assert.Throws<ArgumentException>(() => RecordPatches.DecodeTestDataRowVersion(""));
    }

    [Fact]
    public void SeedingTheCounter_MakesALaterWriteOutrankEveryRestoredRow()
    {
        // The ordering property. Without seeding, the next stamp is 1 and loses to 261,652.
        RowVersionPatches.SeedFromRestoredRowVersion(Expected);
        var next = RowVersionPatches.PeekNextRowVersionForTests();

        Assert.True(next > Expected,
            $"a row written after the restore must outrank every restored row: next={next}, "
            + $"max restored={Expected}");
    }

    [Fact]
    public void SeedingIsHighWaterMark_ANonMaxValueNeverLowersTheCounter()
    {
        // Rows arrive per table in no particular order, so seeding must never move backwards.
        RowVersionPatches.SeedFromRestoredRowVersion(5_000_000);
        var afterHigh = RowVersionPatches.PeekNextRowVersionForTests();

        RowVersionPatches.SeedFromRestoredRowVersion(7);
        var afterLow = RowVersionPatches.PeekNextRowVersionForTests();

        Assert.True(afterLow >= afterHigh,
            $"a lower restored value must not lower the counter: {afterHigh} then {afterLow}");
    }
}
