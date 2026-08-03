// RecordImplementation.SetSecurityFiltering — faithful replacement for the old NoOp2.
//
// BC's original body is:
//
//     private void SetSecurityFiltering(SecurityFiltering filtering)
//     {
//         securityFiltering = filtering;
//         InvalidateCurrentResultSetEnumerator();
//         switch (securityFiltering) { Filtered: …; Validated: …; Disallowed: …; }
//     }
//
// The switch arms all funnel through GetSecurityFilters(SecurityFilterType), which walks
// the session permission set and the tenant security-filter tables. Those need service-tier
// permission state the skeleton runtime does not have, which is why this method was
// originally routed to NoOp2.
//
// But NoOp2 dropped the FIRST TWO statements as well — including `securityFiltering =
// filtering`, the field the public SecurityFiltering getter reads. AL code doing
//
//     Rec.SecurityFiltering(SecurityFilter::Ignored);
//     Rec.SecurityFiltering()          // ← still the old value
//
// therefore silently observed no change. That is exactly the silent-fake shape
// `.claude/rules/loud-failures.md` forbids: a green read of a stale value.
//
// FAITHFULNESS ARGUMENT for keeping the switch omitted:
// every arm of that switch is conditioned on GetSecurityFilters(...) returning something
// other than FilterFieldDictionary.Empty. The runner has no record-level security at all —
// no security-filter rows are ever provisioned for the test user — so on real BC with an
// unrestricted user each arm is reached and then immediately breaks out without touching
// the record. Storing the field and invalidating the cached result set is therefore
// observably equivalent for every in-scope AL program.
//
// If record-level security filtering is ever brought in scope, this helper is the seam:
// it must then run the real arms rather than skip them.
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner.Patches;

// Must be public: the rewritten Ncl body calls straight into this helper, and the CLR
// enforces accessibility on that call (an internal helper raises MethodAccessException).
public static class SecurityFilteringPatches
{
    private static FieldInfo? _fSecurityFiltering;
    private static PropertyInfo? _pResultSetEnumerator;
    private static FieldInfo? _fResultSetReversed;
    private static bool _resolved;

    /// <summary>
    /// Replacement for the private instance method
    /// <c>RecordImplementation.SetSecurityFiltering(SecurityFiltering)</c>.
    /// The enum argument arrives as its underlying <see cref="int"/> on the IL stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RecordImplementation_SetSecurityFiltering(object recImpl, int filtering)
    {
        Resolve(recImpl.GetType());

        // The field is a SecurityFiltering enum; SetValue needs a boxed enum, not a boxed int.
        _fSecurityFiltering!.SetValue(
            recImpl, System.Enum.ToObject(_fSecurityFiltering.FieldType, filtering));

        // InvalidateCurrentResultSetEnumerator() inlined — a mode change must not be served
        // from a result set enumerated under the previous mode.
        _pResultSetEnumerator?.SetValue(recImpl, null);
        _fResultSetReversed?.SetValue(recImpl, false);
    }

    private static void Resolve(System.Type recImplType)
    {
        if (_resolved) return;

        const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        for (var t = recImplType; t != null && _fSecurityFiltering == null; t = t.BaseType)
            _fSecurityFiltering = t.GetField("securityFiltering", Any);

        if (_fSecurityFiltering == null)
            throw new System.InvalidOperationException(
                "[SecurityFiltering] RecordImplementation.securityFiltering field not found — " +
                "Record.SecurityFiltering() would silently report a stale mode.");

        for (var t = recImplType; t != null; t = t.BaseType)
        {
            _pResultSetEnumerator ??= t.GetProperty("ResultSetEnumerator", Any);
            _fResultSetReversed ??= t.GetField("resultSetReversed", Any);
        }

        _resolved = true;
    }
}
