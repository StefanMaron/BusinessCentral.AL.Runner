// AlValueWireFormat — turns a raw AL local's runtime value into a JSON-serializable
// representation. Extracted from AlValueCapture (issue #1640) so both it and
// AlScopeInspector's live variable reads (issue #1642, --dap) render the same value
// the same way, rather than two independently-drifting copies.
//
// #2488 — BY-REFERENCE PARAMETERS ARRIVE WRAPPED:
//
// BC's emitter materialises an AL `var` (by-reference) parameter on the generated
// `*_Scope` class as `Microsoft.Dynamics.Nav.Runtime.ByRef<T>` — a getter/setter pair
// over the CALLER's slot, not a copy of the value (BcCompiler's header: the
// [NavByReferenceAttribute] T -> ByRef<T> wrap happens natively at parameter emission).
// The [NavName] field scan in AlValueCapture/AlScopeInspector therefore reads the
// WRAPPER, and `ByRef<T>` declares no ToString() override (decompiled from Ncl.dll:
// getter/setter/Value/ObjectValue/ObjectType and an implicit conversion, nothing else),
// so object.ToString() ran and the default arm below rendered the CLR type name —
// `Microsoft.Dynamics.Nav.Runtime.ByRef`1[System.Int32]` — with captureError null, i.e.
// indistinguishable to the consumer from a real value. Worse, the type name never
// changes, so AlValueCapture.DiffAndUpdate saw every observation of a by-ref local as
// "unchanged" and suppressed all but the first.
//
// The unwrap reads BC's own `IByRef.ObjectValue` (the non-generic accessor on the same
// type), so nothing BC owns is reimplemented — .claude/rules/precompiled-dll-respect.md
// — and the inner value then goes through the SAME rendering rules as a by-value local:
// an AL Integer inner is a CLR int and stays a JSON number; a NavText inner renders via
// its own ToString(). A wrapper whose getter throws is reported through captureError
// exactly like any other unreadable value, never as a wrapper name
// (.claude/rules/loud-failures.md).
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

public static class AlValueWireFormat
{
    /// <summary>Bound on the ByRef unwrap loop. Real AL never nests these — BC passes an
    /// existing wrapper straight through when a `var` argument feeds another `var`
    /// parameter — so any depth beyond a couple means a cycle, and a cycle must end in a
    /// reported failure rather than a hang.</summary>
    private const int MaxByRefUnwrapDepth = 8;

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

        // Unwrap the by-reference wrapper(s) first, then apply the ordinary rules to the
        // value inside (see the file header). A loop rather than a single unwrap because
        // ObjectValue's static type is `object`: nothing in the type system stops a
        // wrapper resolving to another wrapper, and a self-referential one would spin
        // forever — hence the depth bound, which reports rather than silently returning
        // the wrapper's type name.
        for (int depth = 0; raw is IByRef byRef; depth++)
        {
            if (depth >= MaxByRefUnwrapDepth)
            {
                captureError = $"by-ref value still wrapped after {MaxByRefUnwrapDepth} unwraps";
                return null;
            }
            try { raw = byRef.ObjectValue; }
            catch (Exception ex)
            {
                // The slot the wrapper points at could not be read. Same contract as a
                // throwing ToString() below: no value is faked, and the reason is visible.
                captureError = $"ByRef.ObjectValue threw {ex.GetType().Name}";
                return null;
            }
            // A `var` parameter over a genuinely null value is a genuinely null value —
            // captureError stays null so it reads exactly like a null by-value local.
            if (raw == null) return null;
        }

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
