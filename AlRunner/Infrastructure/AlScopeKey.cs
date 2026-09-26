// AlScopeKey — the one place that turns a live NavMethodScope into the member carrying its
// AL compile-time metadata ([SourceSpans], [SignatureSpan], [NavName], [LocalsNames]).
//
// BC's compiler has two scope shapes (docs/coverage-attribution.md#the-two-scope-shapes-bcs-compiler-emits-and-where-sourcespans-sits):
//   - inline-scope emit (a service tier's default, and the runner's since #4697): the method
//     itself carries the attributes and runs on Ncl's shared ALMethodScope<TLocals>;
//   - scope-class emit: a nested `<Method>_Scope_<id>` class carries them. BC still emits this
//     shape for EVENT PUBLISHERS in inline mode, so both shapes coexist in one assembly.
// Every reader keys on the MemberInfo this returns, never on scope.GetType(): in inline mode
// that type is Ncl's and shared by every method with the same locals tuple.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

internal static class AlScopeKey
{
    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly ConcurrentDictionary<(Type Object, int MethodId), MethodInfo?> _methods = new();

    /// <summary>
    /// The metadata carrier of <paramref name="scope"/>: the AL method an
    /// <see cref="ALMethodScope"/> runs, otherwise the scope's own (nested) type. An
    /// ALMethodScope whose method cannot be found — Ncl's own frames — keys on its type,
    /// which carries no [SourceSpans], so every reader treats it as "no AL source".
    /// </summary>
    public static MemberInfo Of(NavMethodScope scope)
    {
        if (scope is ALMethodScope al && al.ApplicationObject is { } owner
            && FindMethod(owner.GetType(), al.MethodId) is { } method)
            return method;
        return scope.GetType();
    }

    /// <summary>
    /// The method declaring <c>[MethodId(<paramref name="methodId"/>)]</c> on
    /// <paramref name="objectType"/> or a base type — BC's own lookup,
    /// <c>ALMethodScope.GetDeclaringMethodInfo</c>, matches the same attribute. The C# name
    /// cannot be used: overloads and field triggers are renamed (<c>Add_662773449</c>,
    /// <c>Name_a45_OnValidate</c>).
    /// </summary>
    internal static MethodInfo? FindMethod(Type objectType, int methodId) =>
        _methods.GetOrAdd((objectType, methodId), static k =>
        {
            for (var t = k.Object; t != null && t != typeof(object); t = t.BaseType)
                foreach (var m in t.GetMethods(Declared))
                    if (m.GetCustomAttribute<MethodIdAttribute>(inherit: false)?.Id == k.MethodId)
                        return m;
            return null;
        });

    /// <summary>
    /// Every scope key <paramref name="type"/> declares: the type itself when it is a scope
    /// class, and each of its methods that carries <paramref name="sourceSpansAttr"/>.
    /// </summary>
    public static IEnumerable<MemberInfo> DeclaredBy(Type type, Type sourceSpansAttr)
    {
        if (Attribute.IsDefined(type, sourceSpansAttr, inherit: false)) yield return type;
        MethodInfo[] methods;
        try { methods = type.GetMethods(Declared); }
        catch (Exception) { yield break; } // a type whose members cannot load has no scopes to offer
        foreach (var m in methods)
            if (Attribute.IsDefined(m, sourceSpansAttr, inherit: false)) yield return m;
    }

    /// <summary>
    /// <see cref="DeclaredBy"/> for a type whose AL object <paramref name="sourceMap"/> maps,
    /// nothing otherwise — checked first, so a packaged app's methods are never scanned. A type
    /// whose identity cannot be read (a dependency that does not load) declares nothing.
    /// </summary>
    public static IEnumerable<MemberInfo> DeclaredByMapped(Type type, Type sourceSpansAttr, AlSourceLocationMap sourceMap)
    {
        bool mapped;
        try { mapped = sourceMap.ContainsKey(AlCallStackCapture.ParseObjectTypeAndId(type)); }
        catch (Exception) { mapped = false; }
        return mapped ? DeclaredBy(type, sourceSpansAttr) : Array.Empty<MemberInfo>();
    }

    /// <summary>
    /// The outermost type declaring <paramref name="key"/> — the AL object's class
    /// (<c>Codeunit60021</c>, <c>Record18</c>, …).
    /// </summary>
    public static Type ObjectTypeOf(MemberInfo key)
    {
        var t = key as Type ?? key.DeclaringType!;
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t;
    }

    /// <summary>
    /// The methods whose IL runs <paramref name="key"/>'s statements. For a scope class,
    /// its OnRun/OnRunAsync/OnRunEventAsync. For an inline method, the method plus every
    /// compiler-generated body C# split out of it — async state machine, lambdas
    /// (<c>asserterror</c> bodies), local functions — all named <c>&lt;Method&gt;…</c>.
    /// </summary>
    public static IEnumerable<MethodInfo> BodiesOf(MemberInfo key)
    {
        if (key is Type scopeClass)
        {
            foreach (var m in scopeClass.GetMethods(Declared))
                if (m.Name is "OnRun" or "OnRunAsync" or "OnRunEventAsync") yield return m;
            yield break;
        }
        if (key is not MethodInfo method || method.DeclaringType is not { } owner) yield break;
        yield return method;
        var prefix = "<" + method.Name + ">";
        foreach (var m in GeneratedMembers(owner, prefix)) yield return m;
    }

    /// <summary>
    /// Every AL parameter and local visible on <paramref name="scope"/>, each with a reader of
    /// its current value. A scope class holds them as <c>[NavName]</c> fields; an inline
    /// scope holds them in its <c>Target</c> tuple, named in order by the method's
    /// <c>[LocalsNames]</c> — the tuple's trailing return-value slot and fillers have no name
    /// and are skipped. Call <see cref="AlNavNameReflection.EnsureInit"/> first.
    /// </summary>
    public static List<(string Name, Func<object?> Read)> NamedLocals(NavMethodScope scope)
    {
        var result = new List<(string, Func<object?>)>();
        if (scope is ALMethodScope al && Of(scope) is MethodInfo method)
        {
            var names = method.GetCustomAttribute<LocalsNamesAttribute>(inherit: false)?.Names;
            if (names == null || names.Length == 0) return result;
            // One boxed copy per observation: every value read below is as of this instant.
            var items = TupleItems(al.CurrentVariables);
            for (var i = 0; i < names.Length; i++)
            {
                var index = i;
                result.Add((names[i], () => index < items.Count
                    ? items[index]
                    : throw new InvalidOperationException(
                        $"LocalsNames declares {names.Length} locals but the scope's tuple holds {items.Count}")));
            }
            return result;
        }
        foreach (var f in scope.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = AlNavNameReflection.GetAlName(f);
            if (name == null) continue;
            result.Add((name, () => f.GetValue(scope)));
        }
        return result;
    }

    /// <summary>The elements of a boxed <see cref="ValueTuple"/>. <see cref="ITuple"/>
    /// already flattens the <c>Rest</c> nesting of a tuple longer than seven.</summary>
    internal static List<object?> TupleItems(object? tuple)
    {
        var items = new List<object?>();
        if (tuple is ITuple t)
            for (var i = 0; i < t.Length; i++) items.Add(t[i]);
        return items;
    }

    private static IEnumerable<MethodInfo> GeneratedMembers(Type owner, string prefix)
    {
        foreach (var m in owner.GetMethods(Declared))
            if (m.Name.StartsWith(prefix, StringComparison.Ordinal)) yield return m;
        foreach (var nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!nested.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) continue;
            // A state machine type is named after its method; every method on it is its body.
            var whole = nested.Name.StartsWith(prefix, StringComparison.Ordinal);
            foreach (var m in nested.GetMethods(Declared))
                if (whole || m.Name.StartsWith(prefix, StringComparison.Ordinal)) yield return m;
            foreach (var m in GeneratedMembers(nested, prefix)) yield return m;
        }
    }
}
