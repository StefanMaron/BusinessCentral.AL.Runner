using System;
using System.Reflection;

namespace AlRunner.Infrastructure;

/// <summary>
/// Finds a PRIVATE instance member declared somewhere in an object's type hierarchy.
///
/// <para><see cref="Type.GetField(string, BindingFlags)"/> and
/// <see cref="Type.GetMethod(string, BindingFlags)"/> with
/// <see cref="BindingFlags.NonPublic"/> do not return a BASE class's private members, so
/// asking the runtime type of a derived instance for a private member of its base returns
/// null. That bites the runner because BC's own
/// <c>CrmTableConnection.CrmTestDataProvider</c> — the provider behind the <c>'@@test@@'</c>
/// CRM test connection (issue #2725) — derives from <c>TempTableDataProvider</c>, whose
/// <c>primaryTree</c> field and <c>FindImplementation</c> method the runner reflects on.</para>
///
/// <para>The obvious repair is to climb until the type's NAME matches the one that declares
/// the member. That is wrong in the other direction, and it shipped once: the loop
/// <c>while (t.BaseType != null &amp;&amp; t.Name != "TempTableDataProvider") t = t.BaseType;</c>
/// climbs all the way to <see cref="object"/> whenever the name never matches, so a provider
/// that declares the member ITSELF — a test double, or any future BC type that is not literally
/// called TempTableDataProvider — loses it. Both of AlRunner.Tests's RowVersionPatchesTests
/// insert tests failed that way on CI.</para>
///
/// <para>So: walk the hierarchy asking each level for its OWN declarations
/// (<see cref="BindingFlags.DeclaredOnly"/>) and stop at the first level that has one. That is
/// correct for the exact type, for a derived type, and for a type whose name nobody predicted.</para>
/// </summary>
internal static class PrivateMemberLookup
{
    private const BindingFlags Declared =
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>Field named <paramref name="name"/> declared on <paramref name="type"/> or any base, else null.</summary>
    public static FieldInfo? Field(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, Declared);
            if (f != null) return f;
        }
        return null;
    }

    /// <summary>Method named <paramref name="name"/> declared on <paramref name="type"/> or any base, else null.</summary>
    public static MethodInfo? Method(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var m = t.GetMethod(name, Declared);
            if (m != null) return m;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="member"/> was resolved from a type that <paramref name="instance"/>
    /// actually is. Call sites here memoise a <see cref="FieldInfo"/>/<see cref="MethodInfo"/> in a
    /// static field, and a hierarchy walk can legitimately resolve DIFFERENT declaring types for
    /// different providers in one process (a test double and a real BC provider, say). Re-resolving
    /// when the cached member does not belong to this instance keeps the memo from poisoning the
    /// second caller — the cache still holds for every run where one provider shape is in play.
    /// </summary>
    public static bool FitsInstance(MemberInfo? member, object instance) =>
        member?.DeclaringType?.IsInstanceOfType(instance) == true;
}
