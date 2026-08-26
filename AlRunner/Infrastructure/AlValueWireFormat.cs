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
    public static object? ToWireValue(object? raw)
    {
        if (raw == null) return null;
        switch (raw)
        {
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong
                 or float or double or decimal or string:
                return raw;
            default:
                try { return raw.ToString(); }
                catch { return null; } // ToString() itself must never crash a capture
        }
    }
}
