using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>
/// Per-test record of the AL objects a test constructed and the procedure/trigger scopes it
/// entered (#5011), for <c>affectedOnly</c> selection. Statement coverage records nothing for an
/// empty procedure or a page without triggers, so a change adding their first statement would
/// otherwise select no test. See docs/server-mode.md#affectedonly-and-entered-scopes.
///
/// Two observation points, both in runner helpers Ncl already calls on every construction or
/// entry, and both a volatile read unless <see cref="AlCoverageTracker.PerTestEnabled"/> is set:
/// <list type="bullet">
/// <item><see cref="NoteObjectConstructed"/>: <c>NavApplicationObjectBase</c>'s constructor
/// replacement, for every codeunit, page, report, xmlport and query instance.</item>
/// <item><see cref="NoteInlineScopeEntered"/>: <c>ALMethodScope.AssignScopeId</c>, which BC calls
/// only from <c>ALStart</c> — the first thing every inline-emitted AL method does, empty or not.
/// The method id is not assigned yet when the base constructor runs, so it is read here.</item>
/// </list>
/// The other scope shape, a nested scope class, is emitted in inline mode only for event
/// publishers, which cannot contain code (AL0286); their object's instance is recorded either way.
/// </summary>
public static class AlObjectUseTracker
{
    private sealed class Bucket
    {
        public readonly HashSet<Type> Objects = new();
        public readonly HashSet<(Type Owner, int MethodId)> InlineScopes = new();
    }

    /// <summary>What one test used: the AL object classes it constructed, and the scope keys
    /// (<see cref="AlScopeKey"/>) it entered. <see cref="UnresolvedScopeOwners"/> are the object
    /// classes of inline scopes whose method could not be found on them.</summary>
    public sealed record Use(IReadOnlyCollection<Type> Objects, IReadOnlyCollection<MemberInfo> Scopes,
        IReadOnlyCollection<Type> UnresolvedScopeOwners);

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Bucket> _perTest = new(StringComparer.Ordinal);
    private static readonly HashSet<Type> _longLivedObjects = new();
    private static Bucket? _lastBucket;
    private static string? _lastKey;

    public static void ResetPerTest()
    {
        lock (_lock) { _perTest.Clear(); _longLivedObjects.Clear(); _lastBucket = null; _lastKey = null; }
    }

    private static Bucket? CurrentBucket()
    {
        var testKey = AlCoverageTracker.CurrentTestKey;
        if (testKey == null) return null;
        if (ReferenceEquals(testKey, _lastKey) && _lastBucket != null) return _lastBucket;
        if (!_perTest.TryGetValue(testKey, out var b)) _perTest[testKey] = b = new Bucket();
        _lastKey = testKey;
        _lastBucket = b;
        return b;
    }

    /// <summary>An AL object instance was built. Records and tableextensions are #5008's
    /// (<see cref="AlEventRaiseTracker.NoteRecordConstructed"/>) and a test codeunit is the test
    /// itself; none is recorded here. An instance built outside any test, or held where another
    /// test can reach it without building one, goes to the long-lived set.</summary>
    public static void NoteObjectConstructed(object? self, object? parent)
    {
        if (!AlCoverageTracker.PerTestEnabled || self == null) return;
        if (self is NavRecord || self is NavTestCodeunit || IsTableExtension(self.GetType())) return;
        var longLived = AlEventRaiseTracker.OutlivesTheTest(parent);
        lock (_lock)
        {
            var bucket = longLived ? null : CurrentBucket();
            if (bucket != null) bucket.Objects.Add(self.GetType());
            else _longLivedObjects.Add(self.GetType());
        }
    }

    // A tableextension instance is built with a record of its base table, which #5008 keys.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _isTableExtension = new();

    private static bool IsTableExtension(Type t) => _isTableExtension.GetOrAdd(t,
        static type => AlCallStackCapture.ParseObjectTypeAndId(type).Item1 == "TableExtension");

    /// <summary>An inline-emitted AL method started (<c>ALMethodScope.ALStart</c>).</summary>
    public static void NoteInlineScopeEntered(object? scope)
    {
        if (!AlCoverageTracker.PerTestEnabled) return;
        if (scope is not ALMethodScope al || al.ApplicationObject is not { } owner) return;
        lock (_lock) CurrentBucket()?.InlineScopes.Add((owner.GetType(), al.MethodId));
    }

    /// <summary>What each test used since the last <see cref="ResetPerTest"/>.</summary>
    public static Dictionary<string, Use> CollectPerTest()
    {
        var result = new Dictionary<string, Use>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var (testKey, bucket) in _perTest)
            {
                var scopes = new List<MemberInfo>();
                var unresolved = new HashSet<Type>();
                foreach (var (owner, methodId) in bucket.InlineScopes)
                {
                    if (AlScopeKey.FindMethod(owner, methodId) is { } m) scopes.Add(m);
                    else unresolved.Add(owner);
                }
                result[testKey] = new Use(bucket.Objects.ToArray(), scopes, unresolved);
            }
        }
        return result;
    }

    /// <summary>Object classes built outside any one test since the last <see cref="ResetPerTest"/>.</summary>
    public static IReadOnlyCollection<Type> LongLivedObjects()
    {
        lock (_lock) return _longLivedObjects.ToArray();
    }
}
