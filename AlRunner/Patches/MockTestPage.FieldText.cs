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
/// Date, Time and DateTime in the spelling the control shows: the session culture's short date
/// and time (en-US, the runner's pinned session language), not the AL <c>Format()</c> default.
/// The patterns are the measured ones (corpus codeunit 69932, every required leg), written out
/// because the formatter that owns them (<c>NavBaseDateTimeFormatter</c>) is in the assembly the
/// runner cannot load. A blank temporal is <see cref="TestPageBlankTemporalValue"/>'s.
/// </summary>
internal static class TestPageTemporalText
{
    private static NavSession Session => (NavSession)BcRuntime.SkeletonSession!;

    private const string DatePattern = "M/d/yyyy";
    private const string TimePattern = "h:mm:ss tt";
    private const string DateTimePattern = "M/d/yyyy h:mm tt";

    internal static string? Format(NavValue? navValue)
    {
        switch (navValue)
        {
            case NavDateTime dateTime when !dateTime.IsZeroOrEmpty:
                return dateTime.GetClientLocalValue(Session)
                    .ToString(DateTimePattern, CultureInfo.InvariantCulture);
            case NavDate or NavTime when navValue is NavDateTimeValue { IsZeroOrEmpty: false }:
                return FormatObject(LiveNavTestPage.Unwrap(navValue), navValue is NavDate ? NavType.Date : NavType.Time);
            default:
                return null;
        }
    }

    /// <summary>The same rule for ValueToString, which sees only the CLR <c>DateTime</c> and the
    /// control's field type. A UTC-kind value is converted to local first, as BC's formatter
    /// does; BC's ALSetValue hands a DateTime over already local.</summary>
    internal static string? FormatObject(object? value, NavType fieldType)
    {
        if (value is not DateTime dt || dt == default) return null;
        if (fieldType == NavType.DateTime && dt.Kind == DateTimeKind.Utc)
            dt = TimeZoneInfo.ConvertTimeFromUtc(dt, TimeZoneInfo.Local);
        return fieldType switch
        {
            NavType.Date => dt.ToString(DatePattern, CultureInfo.InvariantCulture),
            NavType.Time => dt.ToString(TimePattern, CultureInfo.InvariantCulture),
            NavType.DateTime => dt.ToString(DateTimePattern, CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}

/// <summary>
/// Guid and Duration read as AL's own <c>Format()</c> default does ('{GUID}', '1 hour 2 minutes'),
/// because that is what the control shows; a zero Duration is <c>''</c>. The text comes from BC's
/// own <c>NavFormatEvaluateHelper.Format</c> rather than a copy of its rules.
/// </summary>
internal static class TestPageGuidDurationText
{
    private static NavSession Session => (NavSession)BcRuntime.SkeletonSession!;

    internal static string? Format(NavValue? navValue)
        => navValue switch
        {
            NavGuid => NavFormatEvaluateHelper.Format(Session, navValue),
            NavDuration d => d.IsZeroOrEmpty ? string.Empty : NavFormatEvaluateHelper.Format(Session, navValue),
            _ => null,
        };

    internal static string? FormatObject(object? value)
        => value switch
        {
            Guid g => NavFormatEvaluateHelper.Format(Session, NavGuid.Create(g)),
            TimeSpan ts => ts.Ticks == 0
                ? string.Empty
                : NavFormatEvaluateHelper.Format(Session, NavDuration.CreateFromObject(ts)),
            _ => null,
        };
}
