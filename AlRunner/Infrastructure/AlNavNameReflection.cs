// AlNavNameReflection — shared lookup for BC's own [NavName(...)] attribute, the tag
// the AL compiler puts on every public instance field it lifts an AL local onto in a
// generated `*_Scope` class (see AlValueCapture's file header for how that was
// confirmed). Two call sites need to resolve an AL local's declared name from it:
// AlValueCapture (snapshot at NavMethodScope.Exit(), issue #1640) and AlScopeInspector
// (live read at a paused breakpoint, issue #1642). Factored out so the reflection
// handles are resolved once and the "BC changed shape" guard exists in exactly one
// place — same reasoning as AlSourceSpanCodec's own file header ("Lift the
// span-decoding ... into a shared helper ... Do not duplicate the bit layout").
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

internal static class AlNavNameReflection
{
    private static Type? _tNavNameAttr;
    private static PropertyInfo? _piNavNameName;
    private static bool _reflInit;

    public static void EnsureInit()
    {
        if (_reflInit) return;
        // NavNameAttribute lives alongside NavMethodScope in Ncl.dll.
        var nclAsm = typeof(NavMethodScope).Assembly;
        _tNavNameAttr = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavNameAttribute")
            ?? throw new InvalidOperationException(
                "[al-locals] Microsoft.Dynamics.Nav.Runtime.NavNameAttribute not found in Ncl.dll — BC changed shape, do not ship silently");
        _piNavNameName = _tNavNameAttr.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "[al-locals] NavNameAttribute.Name not found — BC changed shape, do not ship silently");
        _reflInit = true;
    }

    /// <summary>The AL-declared name for <paramref name="field"/>, or null if it does not
    /// carry [NavName] (i.e. is not an AL local/parameter the compiler lifted onto this
    /// scope class). Call <see cref="EnsureInit"/> first.</summary>
    public static string? GetAlName(FieldInfo field)
    {
        if (Attribute.GetCustomAttribute(field, _tNavNameAttr!) is not object navNameAttr) return null;
        return _piNavNameName!.GetValue(navNameAttr) as string ?? field.Name;
    }
}
