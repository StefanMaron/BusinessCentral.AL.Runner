using AlRunner.Patches;

namespace AlRunner.Infrastructure;

/// <summary>
/// Turns subscriber changes into the event keys affectedOnly selects on (#4988): a test is
/// selected when the events it raised in the recording run overlap them. Rules and their
/// reasons: docs/server-mode.md#affectedonly-and-event-subscribers.
/// </summary>
internal static class AffectedEventSelection
{
    internal readonly record struct Result(HashSet<string> Keys, string? ForceFullReason);

    /// <param name="previous">Bindings the recording run's tests ran with; null when not recorded.</param>
    /// <param name="current">Bindings now; null when they could not be read.</param>
    /// <param name="observability">What the recording run could see raised; null when not recorded.</param>
    /// <param name="currentPublisherObjects">AL object classes (<c>Kind|id</c>) of the modules now.</param>
    /// <param name="subscriberCodeChanged">Whether the procedure (or whole object) of a binding is
    /// among this request's changed objects or scopes.</param>
    internal static Result ChangedEventKeys(
        IReadOnlyList<SubscriberBinding>? previous,
        IReadOnlyList<SubscriberBinding>? current,
        EventObservability? observability,
        IReadOnlySet<string> currentPublisherObjects,
        Func<SubscriberBinding, bool> subscriberCodeChanged)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (previous == null || observability == null)
            return new(keys, "no recorded event-subscriber bindings for this bundle's coverage baseline");
        if (current == null)
            return new(keys, "the event-subscriber bindings of this request's modules could not be read");

        var previousIds = previous.Select(b => b.Identity).ToHashSet(StringComparer.Ordinal);
        var currentIds = current.Select(b => b.Identity).ToHashSet(StringComparer.Ordinal);

        // Added, removed or rebound (a rebound binding is one removed and one added identity, so
        // both its old and its new event arrive here), plus any binding whose own code changed:
        // an empty subscriber body records no statement, so coverage cannot see it being filled.
        var affected = previous.Where(b => !currentIds.Contains(b.Identity) || subscriberCodeChanged(b))
            .Concat(current.Where(b => !previousIds.Contains(b.Identity) || subscriberCodeChanged(b)));

        foreach (var b in affected)
        {
            var k = b.EventKey;
            if (k.StartsWith("trig|Table|", StringComparison.Ordinal))
            {
                keys.Add(k);
                keys.Add(AlEventRaiseTracker.UnresolvedTriggerKey);
                continue;
            }
            if (k.StartsWith("trig|", StringComparison.Ordinal))
                return new(keys, $"subscriber {Describe(b)} binds a page trigger event, whose raises are not recorded");
            if (!k.StartsWith("ev|", StringComparison.Ordinal))
                return new(keys, $"subscriber {Describe(b)} binds an event of an object type whose raises are not recorded");
            if (observability.ObservableEventKeys.Contains(k))
            {
                keys.Add(k);
                continue;
            }

            // Not observed by the recording run. If its publisher was a fully seeded request
            // module then, the event did not exist; if the publisher is new, neither did the
            // object. Either way a raise needs changed code, which coverage selects. Anything
            // else (a publisher outside the request modules, or one the seed pass missed) could
            // have been raised unrecorded.
            var publisher = PublisherOf(k);
            if (observability.PublisherObjects.TryGetValue(publisher, out var seeded))
            {
                if (seeded) continue;
                return new(keys, $"subscriber {Describe(b)} binds {publisher}, whose events could not all be recorded");
            }
            if (currentPublisherObjects.Contains(publisher)) continue;
            return new(keys, $"subscriber {Describe(b)} binds an event of {publisher}, published outside this request's modules, whose raises are not recorded");
        }
        return new(keys, null);
    }

    /// <summary>
    /// The keys a changed table or tableextension selects on (#5008), or why a changed
    /// pageextension forces a full run (#5011). A table with no triggers
    /// contributes no statement to any coverage, yet an added trigger, a field property or a key
    /// changes what every test holding a record of it observes. Rules:
    /// docs/server-mode.md#affectedonly-and-changed-tables.
    /// </summary>
    /// <param name="changed">This request's changed objects.</param>
    /// <param name="currentExtensionBases">Each tableextension id to its base table ids now.</param>
    /// <param name="recordedBundleWide">The recording run's <see cref="AlEventRaiseTracker.BundleWideKey"/>
    /// entry; null when it has none.</param>
    internal static Result ChangedTableKeys(
        IEnumerable<(string Kind, int? Id)> changed,
        IReadOnlyDictionary<int, List<int>> currentExtensionBases,
        HashSet<string>? recordedBundleWide)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (kind, id) in changed)
        {
            // A page is keyed by the tests that built it (#5011); an extension's base page is not
            // recorded, so which tests open it is not known.
            if (kind == "PageExtension")
                return new(keys, $"{kind} {id} changed, and the base page of a pageextension is not recorded (#5011)");
            if (kind != "Table" && kind != "TableExtension") continue;
            if (id is not int n)
                return new(keys, $"a changed {kind} has no object id, so the tests holding its records cannot be looked up");
            if (recordedBundleWide == null)
                return new(keys, $"{kind} {n} changed and the coverage baseline has no record of which tests held records of each table");

            keys.Add(AlEventRaiseTracker.UnresolvedTriggerKey);
            if (kind == "Table")
            {
                AddTable(n);
                continue;
            }
            keys.Add(AlEventRaiseTracker.TableKey("TableExtension", n));
            var bases = currentExtensionBases.TryGetValue(n, out var b) ? b : new List<int>();
            foreach (var t in bases) AddTable(t);
            // Removed or edited: the recording run knew its base, and tests holding it carry its key.
            // Added: only the current registry can name the base.
            if (bases.Count == 0 && !recordedBundleWide.Contains(AlEventRaiseTracker.KnownExtensionKey(n)))
                return new(keys, $"the base table of tableextension {n} could not be resolved");
        }

        var held = keys.Where(k => k.StartsWith("tbl|", StringComparison.Ordinal) && recordedBundleWide!.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        if (held != null)
            return new(keys, $"a record of {held.Substring(4).Replace('|', ' ')} was held outside any one test "
                + "(built before the test ran, or by a SingleInstance codeunit), so the tests reading it are not recorded");
        return new(keys, null);

        void AddTable(int table)
        {
            keys.Add(AlEventRaiseTracker.TableKey("Table", table));
            keys.Add(AlEventRaiseTracker.TriggerKey("Table", table));
        }
    }

    /// <summary>In the <see cref="AlEventRaiseTracker.BundleWideKey"/> entry (#5011): an instance of
    /// the object (<c>Kind|id:N</c>) was built outside any one test, or held where another test can
    /// use it without building one or entering its code.</summary>
    internal static string LongLivedObjectKey(string objectKey) => "obj|" + objectKey;

    /// <summary>Why a whole-object change must run everything (#5011): a test can use a long-lived
    /// instance without building it, so which tests did is not recorded. Null when none applies.</summary>
    /// <param name="changedObjectKeys">This request's changed keys; scope keys (<c>::proc:</c>) are
    /// narrowed changes, which the entering tests carry whichever instance they ran on.</param>
    internal static string? LongLivedObjectChange(IEnumerable<string> changedObjectKeys, HashSet<string>? recordedBundleWide)
    {
        if (recordedBundleWide == null) return null;
        foreach (var key in changedObjectKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (key.Contains("::proc:", StringComparison.Ordinal)) continue;
            if (recordedBundleWide.Contains(LongLivedObjectKey(key)))
                return $"an instance of {key.Replace('|', ' ')} was built outside any one test (a test codeunit's global, "
                    + "a SingleInstance codeunit, or before the test ran), so the tests using it are not recorded";
        }
        return null;
    }

    // "ev|Kind|id|Event" -> "Kind|id"
    private static string PublisherOf(string eventKey)
    {
        var parts = eventKey.Split('|');
        return parts.Length >= 3 ? $"{parts[1]}|{parts[2]}" : eventKey;
    }

    private static string Describe(SubscriberBinding b) => $"{b.SubscriberKind} {b.SubscriberId}.{b.ProcedureName}";

    /// <summary>Whether a test's recorded events meet the changed keys. A test with no recorded
    /// set meets any non-empty change. (A changed trigger key always brings
    /// <see cref="AlEventRaiseTracker.UnresolvedTriggerKey"/> with it.)</summary>
    internal static bool Overlaps(HashSet<string>? testEvents, HashSet<string> changedEventKeys)
        => changedEventKeys.Count > 0 && (testEvents == null || testEvents.Overlaps(changedEventKeys));
}
