// BundleDependencyOrder — orders the bundles of one request so a dependency runs BEFORE the
// bundles that consume it, whatever order the caller listed them in (#2614).
//
// THE BUG. A --server request's pre-passes (RunLayeredPrePass / BuildSiblingSourceDeps) run before
// the per-bundle loop and publish every dependency's NEW symbols, so a consuming bundle compiles
// against the post-edit surface and bakes member ids from it. A bundle's runtime ASSEMBLY, though,
// is only reloaded during its own iteration of that loop. List the dependency last — an order
// nothing documents but the runner accepts — and the consumer dispatches its freshly baked ids
// into the assembly still resident from the PREVIOUS request, which does not carry them:
//
//     NavNCLCompilationException: Function ID -53549305 was called.
//     The object with ID 60550 does not have a member with that ID.
//
// #2603 had already removed the silent half of this (the consumer answering with its previous
// binding). What was left was loud but still red, in an order a cold run of the very same sources
// handles fine — so the symbols a bundle compiled against and the assembly it dispatched into came
// from two different points in one request. Running dependencies first restores that invariant.
//
// WHY A SEPARATE FILE. The graph half is pure — given "which bundles are there" and "what does each
// declare", the answer needs no filesystem — while the identity read that feeds it does not. Split
// that way it is directly testable for the three properties that actually matter (dependencies
// ordered, nothing else disturbed, cycle safety), which the end-to-end server test cannot cheaply cover: it costs ~20s
// per case and cannot construct a dependency cycle at all. Same reason BundleRootDeduplication
// (#2136) sits beside this rather than inside Program.cs.
//
// DETERMINISM AND MINIMAL DISTURBANCE, stated precisely because the loose version is wrong. Kahn's
// algorithm here always takes the lowest remaining ORIGINAL index, so the output is a deterministic
// function of the input, and a request whose order already satisfies every dependency comes back as
// the input array itself (by reference — the caller reads that to skip its paired result remap).
//
// What it does NOT promise is that two bundles with no dependency between them keep their relative
// order in every case. A bundle waiting on a dependency is emitted when that dependency lands, and
// unrelated bundles listed after it can become ready first: [a, y, m, z] with y depending on z
// comes back [a, m, z, y], not [a, z, y, m]. y and m are unrelated and still swapped. That is
// inherent to any topological sort — y cannot precede z — and the alternative (deferring ready
// nodes to preserve a doomed relative order) buys nothing. The streamed `test` line order is
// user-visible (docs/server-mode.md), so this is documented there rather than glossed here.
//
// CYCLES ARE NOT ADJUDICATED HERE. A cycle (or any remainder Kahn cannot drain) is appended in the
// caller's original order rather than throwing. Program.cs's ChangedLaterDependencyBundles guard
// still applies to whatever ordering comes out, which is why #2614's suggestion that this sort
// could REPLACE that guard was not taken: for every acyclic request the guard is now a no-op, and
// for the ones this cannot fully order it is the backstop.
using AlRunner.Infrastructure;

namespace AlRunner.Infrastructure;

internal static class BundleDependencyOrder
{
    /// <summary>
    /// True when <paramref name="consumer"/> declares a dependency on <paramref name="dependency"/>:
    /// by declared AppId, falling back to Name+Publisher.
    ///
    /// <para>The same pair <c>RunLayeredPrePass</c> matches on, and for the same reason — a bundle
    /// that declares no id of its own still has to be recognisable as somebody's dependency. An
    /// empty AppId never matches by id, so two identity-less bundles do not become each other's
    /// dependency by both being blank.</para>
    /// </summary>
    internal static bool DependsOn(BundleIdentity consumer, BundleIdentity dependency)
    {
        foreach (var dep in consumer.Dependencies)
        {
            if (dep.AppId != Guid.Empty && dep.AppId == dependency.AppId) return true;
            if (string.Equals(dep.Name, dependency.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dep.Publisher, dependency.Publisher, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// <paramref name="paths"/> reordered so every in-request dependency precedes its consumers.
    /// </summary>
    /// <param name="identityOf">
    /// The declared identity of one path, or null when it has none (no readable app.json). A path
    /// with no identity is never related to anything: it cannot depend on a bundle and cannot be
    /// depended upon, so it simply holds its place.
    /// </param>
    /// <returns>
    /// A new list in execution order, or <paramref name="paths"/> itself when nothing needs to
    /// move — callers use reference equality to skip the result-reordering that pairs with this.
    /// </returns>
    internal static IReadOnlyList<string> Sort(
        IReadOnlyList<string> paths, Func<string, BundleIdentity?> identityOf)
    {
        if (paths.Count < 2) return paths;

        var identities = new BundleIdentity?[paths.Count];
        var known = 0;
        for (var i = 0; i < paths.Count; i++)
            if ((identities[i] = identityOf(paths[i])) != null) known++;
        if (known < 2) return paths;

        var n = paths.Count;
        // dependents[i] = indices that must come AFTER i. indegree[j] = how many in-request
        // dependencies j is still waiting on.
        var dependents = new List<int>[n];
        var indegree = new int[n];
        for (var i = 0; i < n; i++) dependents[i] = new List<int>();

        for (var consumer = 0; consumer < n; consumer++)
        {
            var mine = identities[consumer];
            if (mine == null) continue;
            for (var dependency = 0; dependency < n; dependency++)
            {
                if (dependency == consumer) continue;
                var other = identities[dependency];
                if (other == null || !DependsOn(mine, other)) continue;
                dependents[dependency].Add(consumer);
                indegree[consumer]++;
            }
        }

        var ordered = new List<string>(n);
        var emitted = new bool[n];
        var emittedCount = 0;
        while (emittedCount < n)
        {
            var next = -1;
            for (var i = 0; i < n; i++)
                if (!emitted[i] && indegree[i] == 0) { next = i; break; }   // lowest original index
            if (next < 0) break;                                            // cycle: stop draining
            emitted[next] = true;
            emittedCount++;
            ordered.Add(paths[next]);
            foreach (var dependent in dependents[next]) indegree[dependent]--;
        }
        for (var i = 0; i < n; i++)
            if (!emitted[i]) ordered.Add(paths[i]);      // cyclic remainder, in the caller's order

        // Return the INPUT when the sort changed nothing. Checked on the resulting sequence rather
        // than on "were there any edges at all": a request already in the documented
        // dependency-first order has edges — the consumer's indegree is 1 — and an edge-count test
        // would report it as reordered and make the caller pay a result remap for an order that
        // never moved. The caller reads this by reference, so it has to mean "nothing moved".
        for (var i = 0; i < n; i++)
            if (!ReferenceEquals(ordered[i], paths[i]) && !string.Equals(ordered[i], paths[i], StringComparison.Ordinal))
                return ordered;
        return paths;
    }
}
