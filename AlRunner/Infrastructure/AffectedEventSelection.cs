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
