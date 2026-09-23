using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace AlRunner.Infrastructure;

/// <summary>
/// A stable hash of a serialized payload's STRUCTURE — every member name and type reachable
/// from a root record, walked transitively through the runner's own types.
///
/// <para>It exists because a hand-maintained version number cannot tell two concurrent branches
/// apart (issue #2335). <c>~/.cache/al-runner/bc-symbols</c> is shared by every worktree of this
/// repository, and its key carried only the .app's path, the .app's content hash and an integer
/// someone had to remember to bump. Two branches that each add a field and each bump 16 to 17
/// then read each other's entries — and the failure is not a deserialization error, it is a
/// payload that deserializes CLEANLY with the other branch's fields defaulted to null. A wrong
/// answer replayed from cache, which is the exact failure mode the version's own comments warn
/// about for stale entries, reproduced across branches instead of across time.</para>
///
/// <para>That is not hypothetical. It cost one agent about an hour (a virtual table reading
/// empty on a warm cache and full on a cold one, which looks precisely like a bug in the
/// population code), and on 2026-09-05 four agents worked this repository with three of them
/// adding fields to <c>BcAppSymbolCache</c> — two separate branches reached for the same next
/// integer within hours, and both collisions had to be resolved by hand during a rebase.</para>
///
/// <para>A fingerprint derived from the types cannot drift, because nobody has to remember it:
/// a branch that adds a field gets a different key whether or not it bumps anything, and the
/// version number goes back to meaning "the PARSE changed even though the shape did not" —
/// which a fingerprint genuinely cannot detect and which therefore still needs a human.</para>
///
/// <para>Deliberately structural only. It says nothing about VALUES and nothing about how a
/// field is populated, so it is not a substitute for the version number — both belong in the
/// key, and <see cref="BcAppSymbolCache"/> puts both there.</para>
/// </summary>
internal static class RecordShapeFingerprint
{
    /// <summary>
    /// Hex SHA-256 of the canonical shape description of <paramref name="root"/>, truncated to
    /// 16 characters. Truncation is fine here: this is a cache-key discriminator, not a
    /// security boundary, and 64 bits is far past the point where two concurrently-developed
    /// payload shapes collide by accident.
    /// </summary>
    public static string Of(Type root)
    {
        var canonical = Describe(root);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    /// <summary>
    /// The canonical description itself — exposed so a test can assert WHAT changed the
    /// fingerprint, which a bare hash cannot tell anyone, and so a failure reads as "this member
    /// appeared" rather than "two opaque hex strings differ".
    /// </summary>
    public static string Describe(Type root)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<Type>();
        Walk(root, sb, seen);
        return sb.ToString();
    }

    private static void Walk(Type type, StringBuilder sb, HashSet<Type> seen)
    {
        // A container may hold MORE THAN ONE type -- Dictionary<K,V> is the case that bit
        // (#4505) -- so unwrapping to a single type cannot express it. Walk every contained
        // type, and return: the container itself has no shape of ours to record, and its NAME
        // is already written by TypeName at the member that holds it.
        var contained = Contained(type);
        if (contained is not null)
        {
            foreach (var inner in contained) Walk(inner, sb, seen);
            return;
        }
        // Only the runner's own types are walked into. A BCL type's internals are not ours and
        // do not change with a branch; recursing into them would make the fingerprint depend on
        // the .NET version, which would invalidate every cache entry on an SDK bump for no
        // reason.
        if (!IsOwnType(type)) return;
        // Records reference each other (and, in principle, themselves). Recording the type once
        // makes the walk terminate and keeps the description independent of traversal order.
        if (!seen.Add(type)) return;

        sb.Append(type.FullName).Append('{');
        // Sorted by name, because reflection does NOT guarantee member order — the runtime is
        // free to return properties in metadata order, which can change with an unrelated edit
        // to the source file. An unsorted walk would produce a fingerprint that changes when
        // nothing about the shape did, which is the mirror of the defect this fixes.
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            sb.Append(p.Name).Append(':').Append(TypeName(p.PropertyType)).Append(';');
        }
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                              .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            sb.Append(f.Name).Append(':').Append(TypeName(f.FieldType)).Append(';');
        }
        sb.Append('}');

        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .OrderBy(p => p.Name, StringComparer.Ordinal))
            Walk(p.PropertyType, sb, seen);
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                              .OrderBy(f => f.Name, StringComparer.Ordinal))
            Walk(f.FieldType, sb, seen);
    }

    /// <summary>
    /// The name recorded for a member's type. Generic arguments are spelled out, so
    /// <c>List&lt;ParsedField&gt;</c> becoming <c>List&lt;ParsedFieldV2&gt;</c> changes the
    /// fingerprint even when the member name does not.
    /// </summary>
    private static string TypeName(Type t)
    {
        if (t.IsArray) return TypeName(t.GetElementType()!) + "[]";
        if (!t.IsGenericType) return t.FullName ?? t.Name;
        var args = string.Join(",", t.GetGenericArguments().Select(TypeName));
        var name = t.GetGenericTypeDefinition().FullName ?? t.Name;
        return $"{name}<{args}>";
    }

    /// <summary>
    /// The types a container holds, or <c>null</c> when <paramref name="t"/> is not a container
    /// and so carries its own shape.
    /// </summary>
    /// <remarks>
    /// Returns a LIST rather than one type because a container may hold several:
    /// <c>Dictionary&lt;K,V&gt;</c> holds two, and a record reachable only as its VALUE was
    /// never walked into. Gaining a member then left the fingerprint unchanged, so the cache
    /// served a stale payload at the identical key -- silent, and warm-only, because CI
    /// provisions a fresh cache on every leg (#4505).
    ///
    /// <para><see cref="Walk"/> records members only for a runner-owned type, and reaches
    /// THROUGH a type only when this method names it. Both together give the rule, which is
    /// recursive rather than a two-way test: <b>a type is reached only if every container on
    /// the path from the payload to it is named here.</b></para>
    ///
    /// <para>Named rather than "any generic type" because walking an arbitrary BCL generic
    /// would tie the cache key to the SDK version, which <see cref="IsOwnType"/> argues
    /// against. So an unnamed container hides whatever it holds, at any depth and whatever its
    /// arity: <c>HashSet&lt;Leaf&gt;</c> misses, and so does <c>List&lt;HashSet&lt;Leaf&gt;&gt;</c>
    /// despite the named outer one (measured). <c>HashSet</c>, <c>Queue</c>, <c>ISet</c>,
    /// <c>IReadOnlyCollection</c>, <c>Tuple</c>, <c>ValueTuple</c>, <c>ConcurrentDictionary</c>
    /// and <c>SortedDictionary</c> are all unnamed today. <b>Add a container here before putting
    /// it in a payload.</b></para>
    ///
    /// <para>The trap the old form set is that <c>TypeName</c> still spelled the value type's
    /// name into the description, so the obvious probe -- two dictionaries whose value types
    /// have DIFFERENT names -- passes against the defect and reads as coverage.</para>
    /// </remarks>
    private static IReadOnlyList<Type>? Contained(Type t)
    {
        if (t.IsArray) return new[] { t.GetElementType()! };
        if (!t.IsGenericType) return null;
        var def = t.GetGenericTypeDefinition();
        if (def == typeof(Nullable<>) || def == typeof(List<>) || def == typeof(IReadOnlyList<>)
            || def == typeof(IList<>) || def == typeof(IEnumerable<>) || def == typeof(ICollection<>)
            || def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>)
            || def == typeof(IReadOnlyDictionary<,>) || def == typeof(KeyValuePair<,>))
            return t.GetGenericArguments();
        return null;
    }

    /// <summary>
    /// Whether the walk descends INTO a type's members. The rule is "not a platform type",
    /// deliberately not "declared in this assembly": the latter was the first version and it was
    /// wrong twice over — it made the helper untestable with records declared in the test
    /// assembly (every fingerprint came back identical, because the walk returned immediately
    /// and described nothing), and it would silently stop walking if a payload record were ever
    /// moved to another project.
    ///
    /// <para>System and Microsoft namespaces are excluded so the fingerprint does not depend on
    /// the .NET version or on BC's own types — walking those would invalidate every cache entry
    /// on an SDK bump for no reason. Their NAMES are still recorded by the caller, so a member
    /// changing from <c>string</c> to <c>int</c> is still visible; only their internals are
    /// out of bounds.</para>
    /// </summary>
    private static bool IsOwnType(Type t)
    {
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(Guid) || t == typeof(TimeSpan))
            return false;
        var ns = t.Namespace;
        if (ns == null) return false;
        return !ns.StartsWith("System", StringComparison.Ordinal)
            && !ns.StartsWith("Microsoft", StringComparison.Ordinal);
    }
}
