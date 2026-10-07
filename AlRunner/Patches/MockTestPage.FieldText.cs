// MockTestPage.FieldText.cs — the text a TestPage control shows for a Date, Time, DateTime,
// Guid, Duration, or a blanked number (#5369, #5411). The Decimal arm is TestPageNumericValue.
// Claims and measurements: docs/limitations.md#testpage-field-text.
using System;
using System.Globalization;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner;

/// <summary>
/// BlankZero / BlankNumbers: a blanked number reads <c>''</c> on both sides of AssertEquals.
/// A port of <c>NavBlankNumbersDecorator.BlankFormatApplies</c> and its three specifier
/// predicates (Microsoft.Dynamics.Nav.Client.UI, 28.1), which the runner cannot load (Framework.UI
/// imports user32, see Win32Stubs). Re-check both if BC changes the decorator.
/// </summary>
internal static class TestPageBlanking
{
    internal readonly record struct Spec(BlankNumbers Numbers, bool Zero)
    {
        internal bool IsNone => Numbers == BlankNumbers.DontBlank && !Zero;
    }

    private static bool BlankZeroSpecifier(Spec s)
        => s.Zero || s.Numbers is BlankNumbers.BlankZero or BlankNumbers.BlankNegAndZero or BlankNumbers.BlankZeroAndPos;
    private static bool BlankNegSpecifier(Spec s)
        => s.Numbers is BlankNumbers.BlankNegAndZero or BlankNumbers.BlankNeg;
    private static bool BlankPosSpecifier(Spec s)
        => s.Numbers is BlankNumbers.BlankPos or BlankNumbers.BlankZeroAndPos;

    private static bool ByNumber(Spec s, int sign)
        => (sign == 0 && BlankZeroSpecifier(s)) || (sign < 0 && BlankNegSpecifier(s)) || (sign > 0 && BlankPosSpecifier(s));

    /// <summary>The CLR value of a field as BC's formatter sees it: int (Integer, Option, Enum),
    /// long, decimal, bool, DateTime (Date, Time, DateTime) or TimeSpan.</summary>
    internal static bool Applies(Spec? spec, object? value)
    {
        if (spec is not { } s || s.IsNone || value == null) return false;
        return value switch
        {
            int i => ByNumber(s, Math.Sign(i)),
            long l => ByNumber(s, Math.Sign(l)),
            decimal d => ByNumber(s, Math.Sign(d)),
            bool b => s.Zero && !b,
            DateTime dt => dt == DateTime.MinValue && BlankZeroSpecifier(s),
            TimeSpan ts => ts.Ticks == 0 && BlankZeroSpecifier(s),
            _ => false,
        };
    }

    internal static string? Format(NavValue? navValue, Spec? spec)
        => navValue != null && Applies(spec, LiveNavTestPage.Unwrap(navValue)) ? string.Empty : null;

    internal static string? FormatObject(object? value, Spec? spec)
        => Applies(spec, value) ? string.Empty : null;
}

/// <summary>
/// Date, Time and DateTime in the spelling the control shows: the en-US culture's short date,
/// long time and short date-time patterns (the runner's pinned session language is 1033), not the
/// AL <c>Format()</c> default. The patterns come from the runtime's culture data, as BC's own
/// formatter takes them, so the space before AM/PM is whatever that data holds (U+202F from CLDR
/// 42 on). A blank temporal is <see cref="TestPageBlankTemporalValue"/>'s.
/// Measured: corpus codeunit 69932 (docs/limitations.md#testpage-field-text).
/// </summary>
internal static class TestPageTemporalText
{
    private static NavSession Session => (NavSession)BcRuntime.SkeletonSession!;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    internal static string? Format(NavValue? navValue)
    {
        switch (navValue)
        {
            case NavDateTime dateTime when !dateTime.IsZeroOrEmpty:
                return dateTime.GetClientLocalValue(Session)
                    .ToString("g", Culture);
            case NavDate or NavTime when navValue is NavDateTimeValue { IsZeroOrEmpty: false }:
                return FormatObject(LiveNavTestPage.Unwrap(navValue), navValue is NavDate ? NavType.Date : NavType.Time);
            default:
                return null;
        }
    }

    /// <summary>The same rule for ValueToString, which sees only the CLR <c>DateTime</c> and the
    /// control's field type. A UTC-kind value is converted to local first, as BC's formatter
    /// does; BC's ALSetValue hands a DateTime over already local.</summary>
    internal static string? FormatObject(object? value, NavType fieldType, TimeZoneInfo? zone = null)
    {
        if (value is not DateTime dt || dt == default) return null;
        if (fieldType == NavType.DateTime && dt.Kind == DateTimeKind.Utc)
            dt = TimeZoneInfo.ConvertTimeFromUtc(dt, zone ?? TimeZoneInfo.Local);
        return fieldType switch
        {
            NavType.Date => dt.ToString("d", Culture),
            NavType.Time => dt.ToString("T", Culture),
            NavType.DateTime => dt.ToString("g", Culture),
            _ => null,
        };
    }
}

/// <summary>
/// Guid and Duration read as AL's own <c>Format()</c> default does ('{GUID}', '1 hour 2 minutes',
/// and '' for a zero Duration, as the control shows), because that is the text the control shows.
/// It comes from BC's own <c>NavFormatEvaluateHelper.Format</c> rather than a copy of its rules.
/// </summary>
internal static class TestPageGuidDurationText
{
    private static NavSession Session => (NavSession)BcRuntime.SkeletonSession!;

    internal static string? Format(NavValue? navValue)
        => navValue switch
        {
            NavGuid => NavFormatEvaluateHelper.Format(Session, navValue),
            NavDuration => NavFormatEvaluateHelper.Format(Session, navValue),
            _ => null,
        };

    internal static string? FormatObject(object? value)
        => value switch
        {
            Guid g => NavFormatEvaluateHelper.Format(Session, NavGuid.Create(g)),
            TimeSpan ts => NavFormatEvaluateHelper.Format(Session, NavDuration.CreateFromObject(ts)),
            _ => null,
        };
}

/// <summary>
/// A Media control shows the media's id, and a MediaSet control shows the id of the FIRST media in
/// the set (not the set's own id), both lowercase and hyphenated; either shows nothing when no
/// media is set (corpus codeunits 69932 and 69934, every cloud leg and the Windows nightly). A set
/// of several media was not probed. Without this arm the getter answered the CLR type name of the
/// value's payload.
/// </summary>
internal static class TestPageMediaText
{
    internal static string? Format(NavValue? navValue)
        => navValue switch
        {
            NavMediaSet set => set.ALCount == 0 ? string.Empty : Id(set.ALItem(1)),
            NavMediaValueBase media => Id(media.ALMediaId),
            _ => null,
        };

    private static string Id(Guid id)
        => id == Guid.Empty ? string.Empty : id.ToString("D", CultureInfo.InvariantCulture);
}
