// MetadataObjectDiff — a TOTAL, reflective comparison of two BC metadata objects.
//
// The point of "total": this walks every readable instance member the type has, found by
// reflection at run time, rather than a list of members someone chose to check. A test that
// names its properties cannot fail on the property nobody thought about, which is exactly how
// the runner shipped 5,241 wrong `Editable` values and 3,760 wrong `DataClassification`
// values while every metadata test was green (issue #3533).
//
// So: no member allowlist HERE. What is known-different is declared once, with a reason, in
// MetadataDifferenceAllowlist, and everything else is a failure. A BC version that adds a
// property therefore surfaces as a new, undeclared difference.
//
// Nothing about this file is BC-specific in its types — it takes `object` and reflects — so
// it is unit-testable with plain C# fixtures and no service tier. See
// docs/metadata-equivalence.md for how it is driven and what the ground truth is.

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace AlRunner.Metadata;

/// <summary>One member on which the two sides disagree.</summary>
/// <param name="ObjectKey">The object being compared, e.g. <c>Table 1180</c>.</param>
/// <param name="Path">Where in the object graph, e.g. <c>Fields[id=7].Editable</c>.</param>
/// <param name="DeclaringType">Simple name of the type declaring the member, e.g. <c>MetaField</c>.</param>
/// <param name="Member">The member's own name, or <see cref="MetadataObjectDiff.PresenceMember"/>.</param>
/// <param name="Expected">Ground truth — what BC's emitter produced.</param>
/// <param name="Actual">What the runner derived.</param>
public sealed record MetadataDifference(
    string ObjectKey,
    string Path,
    string DeclaringType,
    string Member,
    string Expected,
    string Actual)
{
    /// <summary>The <c>Type.Member</c> pair an allowlist entry declares.</summary>
    public string Signature => DeclaringType + "." + Member;

    public override string ToString()
        => $"{ObjectKey} {Path}: expected '{Expected}', got '{Actual}'";
}

public sealed class MetadataObjectDiffOptions
{
    /// <summary>
    /// How deep the walk may go before it stops. Exceeding it is REPORTED as a difference
    /// rather than silently truncating — a walk that quietly stops short is the same class of
    /// wrong answer this whole harness exists to prevent.
    /// </summary>
    public int MaxDepth { get; init; } = 12;

    /// <summary>
    /// Namespace prefixes whose objects are walked member-by-member. Anything else is a leaf
    /// rendered through <c>ToString()</c>. Defaults to BC's own metadata assemblies.
    /// </summary>
    public IReadOnlyList<string> RecurseNamespacePrefixes { get; init; } =
        new[] { "Microsoft.Dynamics.Nav." };

    /// <summary>
    /// Collection members whose elements are paired by an <c>Id</c> property instead of by
    /// position, given as <c>DeclaringType.Member</c>. Position is the default because element
    /// ORDER is meaningful in AL (a table's keys, a key's fields); an id-paired member also
    /// gets a synthetic <c>#Ordinal</c> comparison so a reordering is still caught.
    /// </summary>
    public IReadOnlySet<string> PairByIdMembers { get; init; } =
        new HashSet<string>(StringComparer.Ordinal) { "MetaTable.Fields" };
}

public static class MetadataObjectDiff
{
    /// <summary>Member name used when an element exists on one side only.</summary>
    public const string PresenceMember = "<presence>";

    /// <summary>Member name used when the walk hit <see cref="MetadataObjectDiffOptions.MaxDepth"/>.</summary>
    public const string DepthMember = "<depth-limit>";

    public const string Absent = "<absent>";
    public const string Null = "<null>";

    public static IReadOnlyList<MetadataDifference> Compare(
        object? expected, object? actual, string objectKey, MetadataObjectDiffOptions? options = null)
    {
        var opts = options ?? new MetadataObjectDiffOptions();
        var acc = new List<MetadataDifference>();
        var comparedPairs = new HashSet<(object, object)>(PairComparer.Instance);
        Walk(expected, actual, objectKey, path: "", depth: 0, opts, acc, comparedPairs);
        return acc;
    }

    private static void Walk(
        object? expected, object? actual, string objectKey, string path, int depth,
        MetadataObjectDiffOptions opts, List<MetadataDifference> acc,
        HashSet<(object, object)> comparedPairs)
    {
        if (expected is null && actual is null) return;
        if (expected is null || actual is null)
        {
            Add(acc, objectKey, path, TypeNameOf(expected ?? actual), PresenceMember,
                expected is null ? Null : "present", actual is null ? Null : "present");
            return;
        }

        // A pair already compared elsewhere in the graph adds nothing on a second visit, and
        // stopping here is what terminates cycles (MetaTable.FieldsById holds the very
        // MetaField instances MetaTable.Fields holds).
        if (!comparedPairs.Add((expected, actual))) return;

        if (depth > opts.MaxDepth)
        {
            Add(acc, objectKey, path, TypeNameOf(expected), DepthMember,
                $"walk stopped at depth {depth}", $"walk stopped at depth {depth}");
            return;
        }

        var type = expected.GetType();
        if (actual.GetType() != type)
        {
            Add(acc, objectKey, path, type.Name, "<type>", type.FullName ?? type.Name,
                actual.GetType().FullName ?? actual.GetType().Name);
            return;
        }

        foreach (var (name, get) in ReadableMembers(type))
        {
            var memberPath = path.Length == 0 ? name : path + "." + name;
            object? ev, av;
            string? ethrow = null, athrow = null;
            try { ev = get(expected); } catch (Exception ex) { ev = null; ethrow = Throw(ex); }
            try { av = get(actual); } catch (Exception ex) { av = null; athrow = Throw(ex); }

            if (ethrow != null || athrow != null)
            {
                if (ethrow != athrow)
                    Add(acc, objectKey, memberPath, type.Name, name,
                        ethrow ?? Render(ev, opts), athrow ?? Render(av, opts));
                continue;
            }

            if (IsCollection(ev, opts) || IsCollection(av, opts))
            {
                CompareCollection(ev, av, objectKey, memberPath, type.Name + "." + name,
                    depth, opts, acc, comparedPairs);
                continue;
            }

            if (ShouldRecurse(ev, opts) || ShouldRecurse(av, opts))
            {
                // Attributed HERE, where the member is known. Left to Walk's own null branch
                // it would be reported against the VALUE's type — `MultiLanguage.<presence>`
                // for both CaptionML and OptionCaptionML — and one allowlist entry would then
                // cover two unrelated members.
                if (ev is null || av is null)
                    Add(acc, objectKey, memberPath, type.Name, name + "." + PresenceMember,
                        ev is null ? Null : "present", av is null ? Null : "present");
                else
                    Walk(ev, av, objectKey, memberPath, depth + 1, opts, acc, comparedPairs);
                continue;
            }

            var es = Render(ev, opts);
            var @as = Render(av, opts);
            if (!string.Equals(es, @as, StringComparison.Ordinal))
                Add(acc, objectKey, memberPath, type.Name, name, es, @as);
        }
    }

    private static void CompareCollection(
        object? expected, object? actual, string objectKey, string path, string memberSignature,
        int depth, MetadataObjectDiffOptions opts, List<MetadataDifference> acc,
        HashSet<(object, object)> comparedPairs)
    {
        // A DICTIONARY has no order, so pairing its entries by position compares whichever
        // entries happen to sit at the same index and reports every one of them as different.
        // Measured before this branch existed: MetaTable.FieldsById produced 20 `MetaField.Id`,
        // 20 `DataColumnName`, 13 `Type`, 13 `IsSystemField` and 8 `Length` "differences" on
        // Business Foundation alone, none of them real — the two sides simply insert in
        // different orders. Paired by key they reduce to the entries that genuinely differ,
        // and the values themselves were already compared under Fields[], so the pair-visited
        // set short-circuits them.
        if (TryMaterializeKeyed(expected, out var ke) && TryMaterializeKeyed(actual, out var ka))
        {
            foreach (var key in ke.Keys.Concat(ka.Keys).Distinct(StringComparer.Ordinal)
                         .OrderBy(k => k, StringComparer.Ordinal))
            {
                var entryPath = $"{path}[key={key}]";
                var hasE = ke.TryGetValue(key, out var ev);
                var hasA = ka.TryGetValue(key, out var av);
                if (!hasE || !hasA)
                {
                    Add(acc, objectKey, entryPath, SplitType(memberSignature), PresenceFor(memberSignature),
                        hasE ? Describe(ev, opts) : Absent, hasA ? Describe(av, opts) : Absent);
                    continue;
                }
                Walk(ev, av, objectKey, entryPath, depth + 1, opts, acc, comparedPairs);
            }
            return;
        }

        var e = Materialize(expected);
        var a = Materialize(actual);

        // A collection of scalars is a value, not a structure: rendering it whole says more in
        // one line than N positional differences would.
        if (e.All(x => IsScalar(x, opts)) && a.All(x => IsScalar(x, opts)))
        {
            var es = "[" + string.Join(",", e.Select(x => Render(x, opts))) + "]";
            var @as = "[" + string.Join(",", a.Select(x => Render(x, opts))) + "]";
            if (!string.Equals(es, @as, StringComparison.Ordinal))
                Add(acc, objectKey, path, SplitType(memberSignature), SplitMember(memberSignature), es, @as);
            return;
        }

        if (opts.PairByIdMembers.Contains(memberSignature)
            && TryPairById(e, a, out var byId))
        {
            // Ordinals are only meaningful while both sides hold the SAME set of ids. One
            // side missing an element shifts every ordinal after it, and reporting that
            // cascade would bury the one difference that caused it — the exact noise
            // id-pairing exists to remove.
            var sameIdSet = byId.All(x => x.E is not null && x.A is not null);
            foreach (var (id, ei, ai, eo, ao) in byId)
            {
                var elemPath = $"{path}[id={id}]";
                if (ei is null || ai is null)
                {
                    Add(acc, objectKey, elemPath, SplitType(memberSignature), PresenceFor(memberSignature),
                        ei is null ? Absent : "present", ai is null ? Absent : "present");
                    continue;
                }
                if (sameIdSet && eo != ao)
                    Add(acc, objectKey, elemPath + ".#Ordinal", TypeNameOf(ei), "#Ordinal",
                        eo.ToString(CultureInfo.InvariantCulture), ao.ToString(CultureInfo.InvariantCulture));
                Walk(ei, ai, objectKey, elemPath, depth + 1, opts, acc, comparedPairs);
            }
            return;
        }

        for (int i = 0; i < Math.Max(e.Count, a.Count); i++)
        {
            var elemPath = $"{path}[{i}]";
            var ei = i < e.Count ? e[i] : null;
            var ai = i < a.Count ? a[i] : null;
            if (ei is null || ai is null)
            {
                Add(acc, objectKey, elemPath, SplitType(memberSignature), PresenceFor(memberSignature),
                    ei is null ? Absent : Describe(ei, opts), ai is null ? Absent : Describe(ai, opts));
                continue;
            }
            Walk(ei, ai, objectKey, elemPath, depth + 1, opts, acc, comparedPairs);
        }
    }

    private static bool TryPairById(
        IReadOnlyList<object?> e, IReadOnlyList<object?> a,
        out List<(string Id, object? E, object? A, int EOrd, int AOrd)> paired)
    {
        paired = new List<(string, object?, object?, int, int)>();
        var ek = new Dictionary<string, (object Obj, int Ord)>(StringComparer.Ordinal);
        var ak = new Dictionary<string, (object Obj, int Ord)>(StringComparer.Ordinal);
        if (!Index(e, ek) || !Index(a, ak)) return false;

        foreach (var key in ek.Keys.Concat(ak.Keys).Distinct(StringComparer.Ordinal)
                     .OrderBy(k => k, StringComparer.Ordinal))
        {
            ek.TryGetValue(key, out var ev);
            ak.TryGetValue(key, out var av);
            paired.Add((key, ev.Obj, av.Obj, ev.Ord, av.Ord));
        }
        return true;

        static bool Index(IReadOnlyList<object?> src, Dictionary<string, (object, int)> into)
        {
            for (int i = 0; i < src.Count; i++)
            {
                var o = src[i];
                if (o is null) return false;
                var p = o.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                if (p is null || p.PropertyType != typeof(int)) return false;
                var key = Convert.ToString(p.GetValue(o), CultureInfo.InvariantCulture) ?? "";
                if (!into.TryAdd(key, (o, i))) return false;   // duplicate ids: fall back to position
            }
            return true;
        }
    }

    // ---- member enumeration -------------------------------------------------------------

    private static readonly Dictionary<Type, (string Name, Func<object, object?> Get)[]> _members = new();

    /// <summary>
    /// Every readable instance member of a type: public and non-public properties without index
    /// parameters, plus non-public fields that are NOT auto-property backing fields (those are
    /// already covered by their property, and reading both would double every difference).
    /// A field with no property is real state and IS compared — <c>MetaTable.fieldsById</c> is one.
    /// </summary>
    internal static (string Name, Func<object, object?> Get)[] ReadableMembers(Type type)
    {
        lock (_members)
        {
            if (_members.TryGetValue(type, out var cached)) return cached;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var list = new List<(string, Func<object, object?>)>();

            foreach (var p in type.GetProperties(flags)
                         .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var pi = p;
                list.Add((pi.Name, o => pi.GetValue(o)));
            }

            foreach (var f in type.GetFields(flags)
                         .Where(f => !f.Name.Contains('<'))
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var fi = f;
                list.Add(("#" + fi.Name, o => fi.GetValue(o)));
            }

            var arr = list.ToArray();
            _members[type] = arr;
            return arr;
        }
    }

    // ---- value classification ----------------------------------------------------------

    private static bool ShouldRecurse(object? v, MetadataObjectDiffOptions opts)
    {
        if (v is null || v is string) return false;
        var t = v.GetType();
        // An enum is a VALUE, not a structure. Without this it recurses into the enum's own
        // `#value__` storage field, and every enum-valued difference is then reported against
        // the enum type — `CompilationTarget.#value__` instead of `MetaTable.Scope` — so the
        // allowlist cannot name the member that actually differs and one entry would cover
        // every property sharing that enum type.
        if (t.IsEnum) return false;
        var full = t.FullName ?? "";
        return opts.RecurseNamespacePrefixes.Any(p => full.StartsWith(p, StringComparison.Ordinal));
    }

    private static bool IsScalar(object? v, MetadataObjectDiffOptions opts)
        => v is null || v is string || v.GetType().IsPrimitive || v.GetType().IsEnum
           || v is decimal || v is Guid || v is DateTime || v is TimeSpan;

    private static bool IsCollection(object? v, MetadataObjectDiffOptions opts)
    {
        if (v is null || v is string) return false;
        if (v is IEnumerable) return true;
        // ImmutableArray<T> is a struct that implements IEnumerable only through its
        // generic interfaces, so `is IEnumerable` alone would miss it.
        var t = v.GetType();
        return t.IsGenericType
               && t.GetInterfaces().Any(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    }

    private static List<object?> Materialize(object? v)
    {
        var list = new List<object?>();
        if (v is null) return list;
        if (v is IEnumerable en && v is not string)
        {
            foreach (var it in en) list.Add(Unwrap(it));
            return list;
        }
        // ImmutableArray<T> / any generic-only enumerable.
        var m = v.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
        if (m is not null)
        {
            var e = m.Invoke(v, null);
            if (e is not null)
            {
                var moveNext = e.GetType().GetMethod("MoveNext")!;
                var current = e.GetType().GetProperty("Current")!;
                while ((bool)(moveNext.Invoke(e, null) ?? false)) list.Add(Unwrap(current.GetValue(e)));
            }
        }
        return list;
    }

    /// <summary>A dictionary enumerates as KeyValuePair; the value is what gets compared.</summary>
    private static object? Unwrap(object? it)
    {
        if (it is null) return null;
        var t = it.GetType();
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            return t.GetProperty("Value")!.GetValue(it);
        return it;
    }

    /// <summary>
    /// True when every element is a KeyValuePair — i.e. this is a dictionary — with the
    /// entries indexed by their key rendered as a string. A duplicate rendered key gives up
    /// rather than silently dropping an entry.
    /// </summary>
    private static bool TryMaterializeKeyed(object? v, out Dictionary<string, object?> keyed)
    {
        keyed = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (v is null) return false;
        var raw = new List<object?>();
        if (v is IEnumerable en && v is not string) { foreach (var it in en) raw.Add(it); }
        else return false;
        if (raw.Count == 0) return false;

        foreach (var it in raw)
        {
            if (it is null) return false;
            var t = it.GetType();
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(KeyValuePair<,>)) return false;
            var key = Convert.ToString(t.GetProperty("Key")!.GetValue(it), CultureInfo.InvariantCulture) ?? "";
            if (!keyed.TryAdd(key, t.GetProperty("Value")!.GetValue(it))) return false;
        }
        return true;
    }

    private static string Render(object? v, MetadataObjectDiffOptions opts)
    {
        if (v is null) return Null;
        if (v is string s) return s.Length == 0 ? "<empty>" : s;
        if (v is bool b) return b ? "True" : "False";
        if (v.GetType().IsEnum) return v.ToString() ?? "";
        if (v is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
        return v.ToString() ?? "";
    }

    private static string Describe(object? v, MetadataObjectDiffOptions opts)
    {
        if (v is null) return Null;
        var id = v.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var name = v.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
        var sb = new StringBuilder(v.GetType().Name);
        if (id is not null) sb.Append(" Id=").Append(Render(SafeGet(id, v), opts));
        if (name is not null) sb.Append(" Name=").Append(Render(SafeGet(name, v), opts));
        return sb.ToString();
    }

    private static object? SafeGet(PropertyInfo p, object o)
    { try { return p.GetValue(o); } catch { return null; } }

    private static string Throw(Exception ex)
        => "<throw:" + (ex.InnerException ?? ex).GetType().Name + ">";

    private static string TypeNameOf(object? o) => o?.GetType().Name ?? "<null>";
    private static string SplitType(string sig) => sig.Split('.')[0];
    private static string SplitMember(string sig) => sig[(sig.IndexOf('.') + 1)..];

    /// <summary>
    /// The member name a presence difference is attributed to — <c>Fields.&lt;presence&gt;</c>
    /// rather than a bare <c>&lt;presence&gt;</c>, so an allowlist entry can cover the runner's
    /// extra FIELDS without also covering its missing FIELD GROUPS. Both used to arrive as
    /// <c>MetaTable.&lt;presence&gt;</c>.
    /// </summary>
    private static string PresenceFor(string memberSignature)
        => SplitMember(memberSignature) + "." + PresenceMember;

    private static void Add(List<MetadataDifference> acc, string objectKey, string path,
        string declaringType, string member, string expected, string actual)
        => acc.Add(new MetadataDifference(objectKey, path, declaringType, member, expected, actual));

    private sealed class PairComparer : IEqualityComparer<(object, object)>
    {
        internal static readonly PairComparer Instance = new();
        public bool Equals((object, object) x, (object, object) y)
            => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);
        public int GetHashCode((object, object) o)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o.Item1) * 397
               ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o.Item2);
    }
}
