// AlValueWireFormat — turns a raw AL local's runtime value into a JSON-serializable
// representation. Extracted from AlValueCapture (issue #1640) so both it and
// AlScopeInspector's live variable reads (issue #1642, --dap) render the same value
// the same way, rather than two independently-drifting copies.
namespace AlRunner.Infrastructure;

public static class AlValueWireFormat
{
    /// <summary>
    /// CLR primitives (AL Integer/Boolean/BigInteger/... map straight to these —
    /// confirmed via DUMP_CS=1 on a probe fixture) pass through as-is so a JSON writer
    /// emits a real JSON number/bool/string. Everything else is a BC value-type wrapper
    /// (NavText, NavCode, NavDate, Decimal18, NavOption, record handles, ...) — those
    /// are precompiled BC types we must not reimplement
    /// (.claude/rules/precompiled-dll-respect.md), so we take their own ToString()
    /// rather than guessing a bespoke encoding per type.
    /// </summary>
    public static object? ToWireValue(object? raw) => ToWireValue(raw, out _);

    /// <summary>
    /// Same conversion as <see cref="ToWireValue(object?)"/>, but surfaces a ToString()
    /// failure via <paramref name="captureError"/> instead of silently flattening it to
    /// <c>null</c> (issue #2043 — a genuinely-null AL variable and one whose ToString()
    /// threw were both reported as the same <c>null</c>, indistinguishable to the
    /// consumer). <paramref name="captureError"/> is null whenever the conversion
    /// succeeded (including the "raw is genuinely null" case), so callers can tell the
    /// two apart. The value returned on a ToString() failure is still <c>null</c> — no
    /// value was ever faked — but now the caller can see WHY.
    /// </summary>
    public static object? ToWireValue(object? raw, out string? captureError)
    {
        captureError = null;
        if (raw == null) return null;
        switch (raw)
        {
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong
                 or float or double or decimal or string:
                return raw;
            default:
                try { return raw.ToString(); }
                catch (Exception ex)
                {
                    // ToString() itself must never crash a capture — but the failure
                    // must be visible, not silently flattened to null (loud-failures.md).
                    captureError = $"ToString() threw {ex.GetType().Name}";
                    return null;
                }
        }
    }
}
