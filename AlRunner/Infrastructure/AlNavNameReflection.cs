// AlNavNameReflection — shared lookup for BC's own [NavName(...)] attribute, the tag
// the AL compiler puts on every public instance field it lifts an AL local onto in a
// generated `*_Scope` class (see AlValueCapture's file header for how that was
// confirmed). Two call sites need to resolve an AL local's declared name from it:
// AlValueCapture (snapshot at NavMethodScope.Exit(), issue #1640) and AlScopeInspector
// (live read at a paused breakpoint, issue #1642). Factored out so the reflection
// handles are resolved once and the "BC changed shape" guard exists in exactly one
// place — same reasoning as AlSourceSpanCodec's own file header ("Lift the
// span-decoding ... into a shared helper ... Do not duplicate the bit layout").
//
// #2042: the SAME [NavName(...)] attribute is also present on the `*_Scope` class
// itself (not just its fields), carrying the AL member name the scope belongs to —
// confirmed via BCCOMPILER_DUMP_CS=1 on AlRunner.Tests/Fixtures/CoverageBranch:
// `[NavName("Run")] private sealed class Run_Scope__1684062386 : ...`. That is the
// SAME string NavMethodScope.ScopeName returns at runtime (AlValueCapture's
// `scopeName`/AlCapturedValue.ScopeName), so AlStatementTable reuses this one lookup
// — taking Type instead of FieldInfo, both being MemberInfo — instead of duplicating
// the attribute-resolution logic for a class instead of a field.
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

    /// <summary>The AL-declared name for <paramref name="member"/>, or null if it does
    /// not carry [NavName]. <paramref name="member"/> is either a <see cref="FieldInfo"/>
    /// (an AL local/parameter lifted onto a scope class) or a <see cref="Type"/> (the
    /// scope class itself, carrying the AL member — procedure/trigger/test method —
    /// name; #2042). Call <see cref="EnsureInit"/> first.</summary>
    public static string? GetAlName(MemberInfo member)
    {
        if (Attribute.GetCustomAttribute(member, _tNavNameAttr!) is not object navNameAttr) return null;
        return _piNavNameName!.GetValue(navNameAttr) as string ?? member.Name;
    }
}
