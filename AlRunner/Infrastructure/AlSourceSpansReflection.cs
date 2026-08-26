// AlSourceSpansReflection — resolves BC's own [SourceSpansAttribute] type + its
// EncodedSpans property once, for the two new --dap (#1642) call sites that need a
// scope class's raw encoded-span array: DapBreakpointResolver (setBreakpoints —
// file+line -> scope type + statement index) and AlDapStackWalker (a paused frame's
// current AL line). AlCoverageTracker (#1922) resolves the same attribute
// independently — its copy is left as-is here rather than retrofitted, so this
// change cannot destabilize the already-shipped --coverage path; see this file's
// PR description for that call.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

internal static class AlSourceSpansReflection
{
    private static Type? _tSourceSpansAttr;
    private static PropertyInfo? _piEncodedSpans;
    private static bool _init;

    public static void EnsureInit()
    {
        if (_init) return;
        var nclAsm = typeof(NavMethodScope).Assembly;
        _tSourceSpansAttr = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.SourceSpansAttribute")
            ?? throw new InvalidOperationException(
                "[dap] Microsoft.Dynamics.Nav.Runtime.SourceSpansAttribute not found in Ncl.dll — BC changed shape, do not ship silently");
        _piEncodedSpans = _tSourceSpansAttr.GetProperty("EncodedSpans", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "[dap] SourceSpansAttribute.EncodedSpans not found — BC changed shape, do not ship silently");
        _init = true;
    }

    /// <summary>The decoded EncodedSpans array for <paramref name="scopeType"/>, or null
    /// if it doesn't carry [SourceSpansAttribute] (e.g. an Ncl-internal scope class, not
    /// AL-compiler-generated) or the array is empty.</summary>
    public static long[]? TryGetSpans(Type scopeType)
    {
        EnsureInit();
        if (Attribute.GetCustomAttribute(scopeType, _tSourceSpansAttr!) is not object srcAttr) return null;
        return _piEncodedSpans!.GetValue(srcAttr) as long[] is { Length: > 0 } spans ? spans : null;
    }
}
