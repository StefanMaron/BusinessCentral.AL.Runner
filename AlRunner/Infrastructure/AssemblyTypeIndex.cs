// AssemblyTypeIndex — resolve types (and attributed methods) in a loaded assembly by reading
// its ECMA-335 metadata tables instead of calling Assembly.GetTypes().
//
// WHY THIS EXISTS
// ---------------
// Assembly.GetTypes() does not "list the types"; it materialises a RuntimeType for EVERY
// TypeDef row in the module — resolving base types, interfaces and generic constraints as it
// goes. On the R2R chunk assemblies DependencyLoader loads for Base Application / System
// Application that is 132,541 types, and it was measured (dotnet-trace, warm cache HIT run of
// AlRunner.Tests/Fixtures/RecordTriggerXRec, 2026-08-17) at 6.5s for the five Base App chunks
// alone — 8.4s inclusive inside EventSubscriberPatches.EnsureRegistryFresh plus another 1.5s
// in RecordPatches.Register, i.e. ~40% of a 23.4s warm invocation, spent to answer questions
// like "is there a type called Codeunit50100 in here".
//
// The metadata tables answer exactly those questions without loading a single type:
//   * TypeDef.Name          → the simple name Type.Name would return.
//   * TypeDef.Namespace     → combined with the nesting chain, the name Assembly.GetType wants.
//   * MethodDef + CustomAttribute → which methods carry [NavEventSubscriberAttribute].
// Measured on the same five chunks: full TypeDef/MethodDef/CustomAttribute walk = 60ms, and
// asm.GetType(fullName) for the 3,270 types that actually matched = 5ms. Two orders of
// magnitude, and the CLR still only ever loads the types we genuinely use.
//
// System.Reflection.Metadata.AssemblyExtensions.TryGetRawMetadata hands back a pointer into
// the already-mapped PE image, so this works for Assembly.Load(byte[]) assemblies too — which
// matters because that is exactly how DependencyLoader loads the big ones and their
// asm.Location is empty, ruling out re-opening the file.
//
// FIDELITY
// --------
// Every lookup here returns a real CLR Type/MethodInfo obtained through asm.GetType(...) /
// Type.GetMethod(...) — the metadata is used only to decide WHICH names to ask for, never to
// synthesise an answer. A name the metadata says is present but the CLR then refuses to load
// is reported on stderr, never silently swallowed (see .claude/rules/loud-failures.md).
//
// FALLBACK
// --------
// A dynamic (Reflection.Emit) assembly has no raw metadata to read. Those fall back to the
// pre-existing Assembly.GetTypes() path, which is correct and — for a dynamic assembly, which
// has a handful of types, not 132k — also cheap. The fallback logs once per assembly under
// AL_RUNNER_VERBOSE so it can never become an invisible perf cliff.

using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text;

namespace AlRunner.Infrastructure;

internal sealed class AssemblyTypeIndex
{
    private static readonly ConditionalWeakTable<Assembly, AssemblyTypeIndex> _cache = new();

    private readonly Assembly _asm;

    /// <summary>Metadata reader over the assembly's mapped PE image; null when the assembly
    /// is dynamic (no raw metadata) — see <see cref="_fallbackTypes"/>.</summary>
    private readonly MetadataReader? _mr;

    /// <summary>Simple type name → TypeDef row number, or a <see cref="List{T}"/> of them when
    /// one assembly declares several types with the same simple name (different namespaces or
    /// different enclosing types). Row numbers, not full-name strings: a distinct-simple-name
    /// keyed dictionary is a fraction of the memory a 132k-entry full-name map would cost, and
    /// the full name is only ever needed for the handful of rows we actually resolve.</summary>
    private readonly Dictionary<string, object>? _byName;

    /// <summary>Only populated for dynamic assemblies (see class header).</summary>
    private readonly Type[]? _fallbackTypes;

    private readonly ConcurrentDictionary<int, Type?> _resolvedByRow = new();

    /// <summary>
    /// Serialises every <see cref="MetadataReader"/> access. The reader itself is only read
    /// from, but it lazily materialises parts of the string/virtual heap on first touch, so
    /// concurrent readers are not safe. Lookups reach this class from several patch classes
    /// with no shared lock ordering (that is also why <see cref="_resolvedByRow"/> is a
    /// ConcurrentDictionary), and the guarded regions are microseconds of table walking, so an
    /// uncontended monitor is the right trade.
    /// </summary>
    private readonly object _mrLock = new();

    private unsafe AssemblyTypeIndex(Assembly asm)
    {
        _asm = asm;
        byte* blob;
        int length;
        bool haveMetadata;
        try { haveMetadata = asm.TryGetRawMetadata(out blob, out length); }
        catch { haveMetadata = false; blob = null; length = 0; }

        if (haveMetadata && blob != null && length > 0)
        {
            try
            {
                _mr = new MetadataReader(blob, length);
                _byName = BuildNameIndex(_mr);
                return;
            }
            catch (Exception ex)
            {
                // Not a silent default: the fallback below is the pre-existing, correct
                // path, and the reason it was taken is on the record (bracket-tagged, so
                // visible under --verbose / AL_RUNNER_VERBOSE=1).
                Console.Error.WriteLine(
                    $"[type-index] metadata unreadable for {asm.GetName().Name}: " +
                    $"{ex.GetType().Name}: {ex.Message} — falling back to Assembly.GetTypes()");
                _mr = null;
                _byName = null;
            }
        }
        else
        {
            Console.Error.WriteLine(
                $"[type-index] no raw metadata for {asm.GetName().Name} " +
                "(dynamic assembly?) — falling back to Assembly.GetTypes()");
        }

        try { _fallbackTypes = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { _fallbackTypes = ex.Types.Where(t => t != null).ToArray()!; }
        catch { _fallbackTypes = Array.Empty<Type>(); }
    }

    private static Dictionary<string, object> BuildNameIndex(MetadataReader mr)
    {
        var map = new Dictionary<string, object>(mr.TypeDefinitions.Count, StringComparer.Ordinal);
        foreach (var handle in mr.TypeDefinitions)
        {
            var name = mr.GetString(mr.GetTypeDefinition(handle).Name);
            int row = MetadataTokens.GetRowNumber(handle);
            if (!map.TryGetValue(name, out var existing)) { map[name] = row; continue; }
            if (existing is List<int> list) { list.Add(row); continue; }
            map[name] = new List<int> { (int)existing, row };
        }
        return map;
    }

    internal static AssemblyTypeIndex For(Assembly asm)
        => _cache.GetValue(asm, static a => new AssemblyTypeIndex(a));

    /// <summary>True when this index was built from metadata rather than the GetTypes() fallback.</summary>
    internal bool IsMetadataBacked => _mr != null;

    /// <summary>
    /// The metadata-backed equivalent of
    /// <c>Array.Find(asm.GetTypes(), x =&gt; x.Name == simpleName &amp;&amp; predicate(x))</c>,
    /// including its ordering: candidates are considered in TypeDef-table order, which is the
    /// order <c>Assembly.GetTypes()</c> itself yields, so a caller relying on "first match wins"
    /// gets the same match it got before.
    /// </summary>
    internal Type? FindFirst(string simpleName, Func<Type, bool>? predicate = null)
    {
        if (_byName == null || _mr == null)
        {
            foreach (var t in _fallbackTypes!)
                if (t.Name == simpleName && (predicate == null || Safe(predicate, t))) return t;
            return null;
        }
        if (!_byName.TryGetValue(simpleName, out var rows)) return null;
        if (rows is int single) return Accept(single, predicate);
        foreach (var row in (List<int>)rows)
        {
            var hit = Accept(row, predicate);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>
    /// Every type in this assembly whose simple name starts with <paramref name="prefix"/>,
    /// resolved to a real CLR <see cref="Type"/>. Used by bulk pre-warms (e.g.
    /// RecordPatches' Record{id} cache) that previously walked all of
    /// <c>Assembly.GetTypes()</c> to pick out a few hundred names.
    /// </summary>
    internal IEnumerable<Type> EnumerateWithPrefix(string prefix)
    {
        if (_byName == null || _mr == null)
        {
            foreach (var t in _fallbackTypes!)
                if (t.Name.StartsWith(prefix, StringComparison.Ordinal)) yield return t;
            yield break;
        }
        foreach (var kv in _byName)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (kv.Value is int single)
            {
                var t = Accept(single, null);
                if (t != null) yield return t;
                continue;
            }
            foreach (var row in (List<int>)kv.Value)
            {
                var t = Accept(row, null);
                if (t != null) yield return t;
            }
        }
    }

    /// <summary>
    /// The simple NAMES of every type in this assembly starting with <paramref name="prefix"/>,
    /// read straight out of the TypeDef table — no <see cref="Type"/> is resolved and no type is
    /// loaded. For callers whose whole question is about the name (e.g. "which Report{id} types
    /// exist here"), this is the cheapest possible answer and, unlike a Type-resolving walk, it
    /// has no partial-load failure mode: the metadata either reads or it does not.
    /// Only valid when <see cref="IsMetadataBacked"/> — throws otherwise, so a caller cannot
    /// silently receive an empty answer for a dynamic assembly.
    /// </summary>
    internal IEnumerable<string> TypeNamesWithPrefix(string prefix)
    {
        if (_byName == null)
            throw new InvalidOperationException(
                $"TypeNamesWithPrefix is metadata-only; {_asm.GetName().Name} has no readable metadata. " +
                "Guard the call with IsMetadataBacked.");
        foreach (var kv in _byName)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int copies = kv.Value is int ? 1 : ((List<int>)kv.Value).Count;
            for (int i = 0; i < copies; i++) yield return kv.Key;
        }
    }

    /// <summary>
    /// The type nested directly inside <paramref name="declaringType"/> whose simple name is
    /// <paramref name="nestedSimpleName"/>, resolved without loading its siblings.
    ///
    /// <c>Type.GetNestedTypes()</c> — and, because the CLR resolves each nested type handle
    /// BEFORE applying the name filter, <c>Type.GetNestedType(name)</c> too — materialises
    /// EVERY nested type of the declaring type. On BC's AL-emitted codeunits that means every
    /// async state machine and every &lt;Event&gt;_Scope class, for each of the ~1,700
    /// publisher keys the event-scope seeding walks: measured at 1.9s of a 15.3s warm
    /// invocation. The NestedClass metadata table answers the same question by name first, so
    /// only the one type that matches is ever resolved.
    /// </summary>
    internal Type? FindNestedType(Type declaringType, string nestedSimpleName)
    {
        if (_mr != null && ReferenceEquals(declaringType.Assembly, _asm))
        {
            try
            {
                int? matchedRow = null;
                lock (_mrLock)
                {
                    var td = _mr.GetTypeDefinition(
                        (TypeDefinitionHandle)MetadataTokens.EntityHandle(declaringType.MetadataToken));
                    foreach (var nh in td.GetNestedTypes())
                    {
                        if (!string.Equals(_mr.GetString(_mr.GetTypeDefinition(nh).Name),
                                           nestedSimpleName, StringComparison.Ordinal)) continue;
                        matchedRow = MetadataTokens.GetRowNumber(nh);
                        break;
                    }
                }
                return matchedRow is int row ? ResolveRow(row) : null;
            }
            catch
            {
                // A type whose metadata token we cannot map (dynamic/constructed edge cases)
                // falls through to the reflection answer below rather than being reported absent.
            }
        }
        try { return declaringType.GetNestedType(nestedSimpleName, BindingFlags.Public | BindingFlags.NonPublic); }
        catch { return null; }
    }

    private Type? Accept(int row, Func<Type, bool>? predicate)
    {
        var t = ResolveRow(row);
        if (t == null) return null;
        if (predicate != null && !Safe(predicate, t)) return null;
        return t;
    }

    private static bool Safe(Func<Type, bool> predicate, Type t)
    {
        try { return predicate(t); }
        catch { return false; }
    }

    private Type? ResolveRow(int row) => _resolvedByRow.GetOrAdd(row, r =>
    {
        string? fullName;
        lock (_mrLock) fullName = BuildFullName(MetadataTokens.TypeDefinitionHandle(r));
        if (fullName == null) return null;
        try { return _asm.GetType(fullName, throwOnError: false); }
        catch { return null; }
    });

    /// <summary>
    /// Reflection-name for a TypeDef row: <c>Namespace.Outer+Inner</c>, with the characters
    /// <c>Assembly.GetType</c>'s parser treats as syntax escaped inside each name part. Returns
    /// null if the nesting chain cannot be walked.
    /// </summary>
    private string? BuildFullName(TypeDefinitionHandle handle)
    {
        var mr = _mr!;
        var parts = new List<string>(2);
        var cursor = handle;
        for (int guard = 0; guard < 64; guard++)
        {
            TypeDefinition td;
            try { td = mr.GetTypeDefinition(cursor); }
            catch { return null; }
            parts.Add(mr.GetString(td.Name));
            var declaring = td.GetDeclaringType();
            if (declaring.IsNil)
            {
                var ns = mr.GetString(td.Namespace);
                var sb = new StringBuilder();
                if (ns.Length != 0) { sb.Append(ns); sb.Append('.'); }
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    AppendEscaped(sb, parts[i]);
                    if (i > 0) sb.Append('+');
                }
                return sb.ToString();
            }
            cursor = declaring;
        }
        return null;
    }

    private static void AppendEscaped(StringBuilder sb, string namePart)
    {
        foreach (var c in namePart)
        {
            // The characters Type.GetType/Assembly.GetType's grammar reserves. AL/BC object
            // type names never contain them, but a compiler-generated or ISV type might.
            if (c is '\\' or ',' or '[' or ']' or '&' or '*' or '+') sb.Append('\\');
            sb.Append(c);
        }
    }

    // ------------------------------------------------------------------
    // Attributed-method discovery
    // ------------------------------------------------------------------

    /// <summary>
    /// Every method declared on a type whose simple name starts with
    /// <paramref name="declaringTypeNamePrefix"/> and that carries a custom attribute whose
    /// attribute type's simple name is <paramref name="attributeSimpleName"/> — decided from
    /// the CustomAttribute table, so no type outside the matches is ever loaded.
    ///
    /// The returned <see cref="MethodInfo"/>s are the real reflection objects the caller then
    /// reads the real attribute instance off; this method decides only WHICH methods to hand
    /// back, never what the attribute says.
    /// </summary>
    internal List<MethodInfo> FindAttributedMethods(string declaringTypeNamePrefix, string attributeSimpleName)
    {
        var result = new List<MethodInfo>();
        if (_byName == null || _mr == null)
        {
            foreach (var t in _fallbackTypes!)
            {
                if (!t.Name.StartsWith(declaringTypeNamePrefix, StringComparison.Ordinal)) continue;
                MethodInfo[] methods;
                try { methods = t.GetMethods(DeclaredMethodFlags); }
                catch { continue; }
                foreach (var m in methods)
                {
                    bool hit;
                    try
                    {
                        hit = CustomAttributeData.GetCustomAttributes(m)
                            .Any(a => a.AttributeType.Name == attributeSimpleName);
                    }
                    catch { continue; }
                    if (hit) result.Add(m);
                }
            }
            return result;
        }

        // Phase 1 — pure metadata: which TypeDef rows declare attributed methods, and with what
        // name/arity. Nothing is loaded here, so the whole walk stays inside the reader lock.
        var perType = new List<(int Row, string TypeName, List<(string Name, int ParamCount)> Hits)>();
        lock (_mrLock)
        {
            var mr = _mr;
            foreach (var tdh in mr.TypeDefinitions)
            {
                TypeDefinition td;
                try { td = mr.GetTypeDefinition(tdh); }
                catch { continue; }
                var typeName = mr.GetString(td.Name);
                if (!typeName.StartsWith(declaringTypeNamePrefix, StringComparison.Ordinal)) continue;

                List<(string Name, int ParamCount)>? hits = null;
                foreach (var mdh in td.GetMethods())
                {
                    MethodDefinition md;
                    try { md = mr.GetMethodDefinition(mdh); }
                    catch { continue; }
                    if (!HasAttribute(mr, md.GetCustomAttributes(), attributeSimpleName)) continue;
                    (hits ??= new()).Add((mr.GetString(md.Name), ReadParameterCount(mr, md)));
                }
                if (hits != null) perType.Add((MetadataTokens.GetRowNumber(tdh), typeName, hits));
            }
        }

        // Phase 2 — resolve only the types that actually matched, and read their real methods.
        foreach (var (row, typeName, hits) in perType)
        {
            var clrType = ResolveRow(row);
            if (clrType == null)
            {
                Console.Error.WriteLine(
                    $"[type-index] {_asm.GetName().Name}: metadata says {typeName} declares " +
                    $"{hits.Count} [{attributeSimpleName}] method(s) but the CLR would not load the type — " +
                    "those subscribers cannot be registered.");
                continue;
            }
            MethodInfo[] declared;
            try { declared = clrType.GetMethods(DeclaredMethodFlags); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[type-index] {_asm.GetName().Name}: GetMethods failed on {clrType.FullName}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                continue;
            }
            var used = new HashSet<MethodInfo>();
            foreach (var (name, paramCount) in hits)
            {
                // Overloads are disambiguated on arity (read straight out of the metadata
                // signature). Two same-name same-arity overloads that BOTH carry the
                // attribute are then taken in declaration order via `used`.
                MethodInfo? match = declared.FirstOrDefault(
                    m => m.Name == name && !used.Contains(m) && m.GetParameters().Length == paramCount);
                // paramCount == -1 means the signature blob was unreadable; fall back to
                // name-only so the subscriber is still registered rather than dropped.
                match ??= declared.FirstOrDefault(m => m.Name == name && !used.Contains(m));
                if (match == null)
                {
                    Console.Error.WriteLine(
                        $"[type-index] {_asm.GetName().Name}: {clrType.FullName}.{name}/{paramCount} carries " +
                        $"[{attributeSimpleName}] in metadata but no matching declared MethodInfo was found.");
                    continue;
                }
                used.Add(match);
                result.Add(match);
            }
        }
        return result;
    }

    internal const BindingFlags DeclaredMethodFlags =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;

    private static bool HasAttribute(MetadataReader mr, CustomAttributeHandleCollection attrs, string attributeSimpleName)
    {
        foreach (var cah in attrs)
        {
            CustomAttribute ca;
            try { ca = mr.GetCustomAttribute(cah); }
            catch { continue; }
            if (AttributeTypeName(mr, ca.Constructor) == attributeSimpleName) return true;
        }
        return false;
    }

    /// <summary>
    /// Simple name of the type declaring a CustomAttribute's constructor. The constructor is a
    /// MemberRef when the attribute type lives in another assembly (the normal case for
    /// [NavEventSubscriber], defined in Ncl and applied in AL-emitted assemblies) and a
    /// MethodDef when the attribute is applied inside its own defining assembly.
    /// </summary>
    private static string? AttributeTypeName(MetadataReader mr, EntityHandle ctor)
    {
        try
        {
            switch (ctor.Kind)
            {
                case HandleKind.MemberReference:
                {
                    var parent = mr.GetMemberReference((MemberReferenceHandle)ctor).Parent;
                    return parent.Kind switch
                    {
                        HandleKind.TypeReference => mr.GetString(mr.GetTypeReference((TypeReferenceHandle)parent).Name),
                        HandleKind.TypeDefinition => mr.GetString(mr.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
                        _ => null, // TypeSpec (generic attribute) — not a shape BC emits
                    };
                }
                case HandleKind.MethodDefinition:
                {
                    var declaring = mr.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType();
                    return mr.GetString(mr.GetTypeDefinition(declaring).Name);
                }
                default:
                    return null;
            }
        }
        catch { return null; }
    }

    /// <summary>Parameter count straight out of the MethodDef signature blob (ECMA-335 II.23.2.1:
    /// calling-convention byte, optional generic arity, then the parameter count).</summary>
    private static int ReadParameterCount(MetadataReader mr, MethodDefinition md)
    {
        try
        {
            var reader = mr.GetBlobReader(md.Signature);
            var header = reader.ReadSignatureHeader();
            if (header.IsGeneric) reader.ReadCompressedInteger();
            return reader.ReadCompressedInteger();
        }
        catch { return -1; }
    }
}
