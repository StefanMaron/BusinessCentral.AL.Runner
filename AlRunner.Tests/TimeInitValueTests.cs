// TimeInitValueTests — #2339.
//
// A Time field's InitValue is not evaluable text on either of the two paths it reaches the
// metatable by, and BC evaluates it with FORMAT 9, the invariant XML format:
// NCLMetaField.InitValue is `ALSystemVariable.EvaluateIntoNavValue(null, ThrowError, this,
// initialValueText, 9)` (Microsoft.Dynamics.Nav.Ncl 28.1, line 157875).
//
//   * From SymbolReference.json it arrives as BC's INTERNAL representation — milliseconds
//     since midnight PLUS ONE. Base App table 1513 "Notification Schedule" field 4 carries
//     "43200001" for `InitValue = 120000T`; table 2161 carries "1" for midnight.
//   * From AL source it arrives as the literal the author wrote, `120000T`.
//
// Neither parses as format 9, so every Init() of such a table threw
// `The value "43200001" can't be evaluated into type Time` — 102 of the tests in Microsoft's
// Tests-SINGLESERVER bucket, all reaching it through
// Notification Schedule.GetTelemetryDimensions on the approval-notification path.
//
// The end-to-end proof is an AL Init() of table 1513 reading back 120000T; these cases pin
// the conversion itself, including the ones that must be LEFT ALONE. Returning false is not a
// failure mode here — it means "hand BC's evaluator the text as written", which is the right
// answer for a spelling this method does not recognise.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class TimeInitValueTests
{
    private static string Normalize(string input)
    {
        Assert.True(RecordPatches.TryNormalizeTimeInitValue(input, out var result),
            $"expected '{input}' to be recognised as a Time InitValue");
        return result;
    }

    [Theory]
    // BC's internal representation: milliseconds since midnight plus one.
    [InlineData("43200001", "12:00:00.000")]   // Base App table 1513 field 4
    [InlineData("1", "00:00:00.000")]          // Base App table 2161 — midnight, not "no value"
    [InlineData("86399999", "23:59:59.998")]
    [InlineData("43200002", "12:00:00.001")]   // the plus-one is real: this is NOT noon
    // AL's own literal, as the source parser hands it over.
    [InlineData("120000T", "12:00:00.000")]
    [InlineData("000000T", "00:00:00.000")]
    [InlineData("235959T", "23:59:59.000")]
    [InlineData("120000.500T", "12:00:00.500")]
    [InlineData("1200T", "00:12:00.000")]      // short forms are right-aligned, as AL writes them
    public void RecognisedSpellings_BecomeInvariantHoursMinutesSeconds(string input, string expected)
        => Assert.Equal(expected, Normalize(input));

    [Theory]
    // Zero means "no value". BC's own GetDefaultNavValue already covers that, and emitting
    // 00:00:00 instead would turn a blank Time into midnight — a different value.
    [InlineData("0")]
    // Out of range in either spelling: hand it to BC rather than invent a wrapped time.
    [InlineData("86400001")]
    [InlineData("256000T")]
    [InlineData("127000T")]
    // Not a Time spelling at all.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noon")]
    [InlineData("12:00:00")]   // already format 9 — leave it exactly as it is
    public void UnrecognisedOrAlreadyCorrect_IsLeftForBcToEvaluate(string input)
        => Assert.False(RecordPatches.TryNormalizeTimeInitValue(input, out _),
            "an unrecognised spelling must reach BC's own evaluator unchanged");
}
