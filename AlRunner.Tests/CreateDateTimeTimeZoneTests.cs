using System;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for AlCompat.SafeLocalToUtc — the wall-clock → UTC conversion
/// AlCompat must apply before storing a DateTime into a NavDateTime's backing
/// value field.
///
/// BC's ALDT2Time / ALDT2Date read NavDateTime.value as a server UTC timestamp
/// and project it to TimeZoneInfo.Local on every read. Writers that store wall-
/// clock ticks directly cause DT2Time(CreateDateTime(D, T)) != T on any non-UTC
/// host. CI runs Linux/UTC, so the gap was invisible there.
///
/// These tests verify the pure conversion function behaves deterministically
/// across timezones — including DST-invalid and DST-ambiguous local times — so
/// the test runner produces the same NavDateTime.value on any host.
///
/// Issue: #1159
/// </summary>
public class CreateDateTimeTimeZoneTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");       // UTC+9, no DST
    private static readonly TimeZoneInfo CentralEurope = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); // UTC+1/+2 with DST

    [Fact]
    public void SafeLocalToUtc_UtcLocal_ReturnsSameTicksAsUtc()
    {
        var wall = new DateTime(2024, 1, 15, 15, 30, 45, DateTimeKind.Unspecified);

        var utc = AlCompat.SafeLocalToUtc(wall, TimeZoneInfo.Utc);

        Assert.Equal(wall.Ticks, utc.Ticks);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void SafeLocalToUtc_NonUtcLocal_SubtractsOffset()
    {
        var wall = new DateTime(2024, 1, 15, 15, 30, 45, DateTimeKind.Unspecified);

        var utc = AlCompat.SafeLocalToUtc(wall, Tokyo);

        // Tokyo UTC+9: wall-clock 15:30 local → 06:30 UTC
        Assert.Equal(new DateTime(2024, 1, 15, 6, 30, 45, DateTimeKind.Utc).Ticks, utc.Ticks);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void SafeLocalToUtc_AmbiguousLocalTime_PicksStandardTimeOffset()
    {
        // CE fall-back: 2024-10-27 02:30 local exists twice. Pick standard-time offset (+1h)
        // deterministically so Windows and Linux always produce the same UTC ticks.
        var ambiguous = new DateTime(2024, 10, 27, 2, 30, 0, DateTimeKind.Unspecified);
        Assert.True(CentralEurope.IsAmbiguousTime(ambiguous));

        var utc = AlCompat.SafeLocalToUtc(ambiguous, CentralEurope);

        // Standard offset is +1 (CET) → UTC 01:30, not DST +2 (CEST) → UTC 00:30.
        Assert.Equal(new DateTime(2024, 10, 27, 1, 30, 0, DateTimeKind.Utc).Ticks, utc.Ticks);
    }

    [Fact]
    public void SafeLocalToUtc_InvalidLocalTime_ShiftsForwardToFirstValidInstant()
    {
        // CE spring-forward: 2024-03-31 02:30 local does not exist. Shift to the first
        // valid instant (03:00 local = 01:00 UTC) deterministically.
        var invalid = new DateTime(2024, 3, 31, 2, 30, 0, DateTimeKind.Unspecified);
        Assert.True(CentralEurope.IsInvalidTime(invalid));

        var utc = AlCompat.SafeLocalToUtc(invalid, CentralEurope);

        Assert.Equal(new DateTime(2024, 3, 31, 1, 0, 0, DateTimeKind.Utc).Ticks, utc.Ticks);
    }

    [Fact]
    public void SafeLocalToUtc_UnambiguousDstTime_UsesActualOffset()
    {
        // In summer, CE is on DST (+2). A wall-clock in mid-summer is not ambiguous and
        // uses the DST offset (not the standard-time fallback that AmbiguousLocalTime test
        // pins). This catches a mutant that always chooses standard-time offset.
        var summer = new DateTime(2024, 7, 15, 14, 0, 0, DateTimeKind.Unspecified);
        Assert.False(CentralEurope.IsAmbiguousTime(summer));
        Assert.False(CentralEurope.IsInvalidTime(summer));

        var utc = AlCompat.SafeLocalToUtc(summer, CentralEurope);

        // CEST is +2h → 14:00 local → 12:00 UTC.
        Assert.Equal(new DateTime(2024, 7, 15, 12, 0, 0, DateTimeKind.Utc).Ticks, utc.Ticks);
    }
}
