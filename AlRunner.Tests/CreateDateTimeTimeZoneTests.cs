using System;
using System.Reflection;
using AlRunner.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
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
[Collection("Pipeline")]
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
    public void PickStandardOffset_StandardFirst_ReturnsFirst()
    {
        // TimeZoneInfo.GetAmbiguousTimeOffsets ordering is undefined per MS docs;
        // both orderings must land on the standard-time offset.
        var std = TimeSpan.FromHours(1);
        var dst = TimeSpan.FromHours(2);

        Assert.Equal(std, AlCompat.PickStandardOffset(new[] { std, dst }, std));
    }

    [Fact]
    public void PickStandardOffset_StandardLast_ReturnsLast()
    {
        // This is the case a regression to `offsets[0]` would miss — it happens to
        // work on Windows (where standard is first) but would silently break on a
        // platform where GetAmbiguousTimeOffsets returns [dst, standard].
        var std = TimeSpan.FromHours(1);
        var dst = TimeSpan.FromHours(2);

        Assert.Equal(std, AlCompat.PickStandardOffset(new[] { dst, std }, std));
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
    public void DaTi2Variant_AppliesLocalToUtcConversion()
    {
        // Contract for the Variant-context path: the Variant produced by BC's
        // ALDaTi2Variant lowering must store the same UTC-converted ticks as
        // ALCreateDateTime would. The AL compiler typically lowers
        // `v := CreateDateTime(d, t)` through ALCreateDateTime + Variant boxing,
        // but this test pins the symmetric behavior in case a BC version or
        // emission pattern reaches AlCompat.DaTi2Variant directly.
        using var scope = new LocalTimeZoneScope("Tokyo Standard Time"); // UTC+9

        var navDate = ConstructNavValue<NavDate>(
            new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Unspecified));
        var navTime = ConstructNavValue<NavTime>(
            new DateTime(1754, 1, 1, 15, 30, 45, DateTimeKind.Unspecified));

        var variant = AlCompat.DaTi2Variant(navDate, navTime);
        var navDt = Assert.IsType<NavDateTime>(variant.Value);
        var stored = AlCompat.GetNavDateTimeValue(navDt);

        // Tokyo wall-clock 2024-01-15 15:30:45 → UTC 06:30:45
        Assert.Equal(
            new DateTime(2024, 1, 15, 6, 30, 45, DateTimeKind.Utc).Ticks,
            stored.Ticks);
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
    }

    private static T ConstructNavValue<T>(DateTime value)
    {
        var inst = Activator.CreateInstance(typeof(T), nonPublic: true)
            ?? throw new InvalidOperationException($"Activator failed on {typeof(T)}");
        var f = typeof(T).BaseType?.GetField("value",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{typeof(T).Name}.value field not found");
        f.SetValue(inst, value);
        return (T)inst;
    }

    /// <summary>
    /// Overrides TimeZoneInfo.Local by writing to the private CachedData._localTimeZone
    /// field; restores on Dispose. Used to make datetime-conversion tests host-independent
    /// without touching the OS registry.
    /// </summary>
    internal sealed class LocalTimeZoneScope : IDisposable
    {
        private readonly TimeZoneInfo _original;
        private readonly FieldInfo _localField;
        private readonly object _cached;

        public LocalTimeZoneScope(string tzId)
        {
            var tziType = typeof(TimeZoneInfo);
            var cachedField = tziType.GetField("s_cachedData",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("TimeZoneInfo.s_cachedData not found");
            _cached = cachedField.GetValue(null)
                ?? throw new InvalidOperationException("s_cachedData instance is null");
            var nested = tziType.GetNestedType("CachedData", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("TimeZoneInfo.CachedData not found");
            _localField = nested.GetField("_localTimeZone",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("CachedData._localTimeZone not found");
            _original = (TimeZoneInfo)(_localField.GetValue(_cached)
                ?? throw new InvalidOperationException("_localTimeZone was null"));
            _localField.SetValue(_cached, TimeZoneInfo.FindSystemTimeZoneById(tzId));
        }

        public void Dispose() => _localField.SetValue(_cached, _original);
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
