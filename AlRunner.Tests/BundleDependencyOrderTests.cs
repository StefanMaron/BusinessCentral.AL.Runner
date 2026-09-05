// BundleDependencyOrderTests — the graph half of #2614.
//
// A --server request's pre-passes publish every dependency's NEW symbols before the per-bundle
// loop, but a bundle's runtime ASSEMBLY is only reloaded during its own iteration of that loop.
// List the dependency last and the consuming bundle dispatches freshly baked member ids into the
// assembly still resident from the previous request:
//
//     NavNCLCompilationException: Function ID -53549305 was called.
//     The object with ID 60550 does not have a member with that ID.
//
// Loud rather than wrong (#2603 removed the silent half), but red in an order a cold run of the
// same sources handles fine. Running dependencies first is the fix.
//
// ServerCrossAppOverloadRebindTests proves the end-to-end effect against a real server process,
// which is the measurement that matters — but it costs ~20s per case and cannot construct a
// dependency cycle at all. These pin the three properties that decide whether the sort is safe to
// apply to EVERY request rather than only the broken one: it moves what must move, it moves
// nothing else, and it never loses or duplicates a bundle.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BundleDependencyOrderTests
{
    private static BundleIdentity App(string name, params string[] dependsOnNames)
        => new(
            AppId: Guid.NewGuid(),
            Name: name,
            Publisher: "AL Runner Fixtures",
            Version: new Version(1, 0, 0, 0),
            RuntimeVersion: new Version(13, 0),
            Dependencies: dependsOnNames
                .Select(n => new DependencyRef(Guid.Empty, n, "AL Runner Fixtures", new Version(1, 0, 0, 0), false))
                .ToList());

    private static IReadOnlyList<string> Sort(
        IReadOnlyList<string> paths, Dictionary<string, BundleIdentity?> identities)
        => BundleDependencyOrder.Sort(paths, p => identities.TryGetValue(p, out var id) ? id : null);

    // ── it moves what must move ────────────────────────────────────────────────────────────────

    [Fact]
    public void ADependencyListedAfterItsConsumer_IsMovedBeforeIt()
    {
        // The exact #2614 shape: sourcePaths = [test-app, app], app being test-app's dependency.
        var ids = new Dictionary<string, BundleIdentity?>
        {
            ["test-app"] = App("XApp Ovl Test App", "XApp Ovl App"),
            ["app"] = App("XApp Ovl App"),
        };

        Assert.Equal(new[] { "app", "test-app" }, Sort(new[] { "test-app", "app" }, ids));
    }

    [Fact]
    public void AThreeLevelChain_IsFullyOrdered_FromTheMostReversedInput()
    {
        // c depends on b depends on a, listed exactly backwards.
        var ids = new Dictionary<string, BundleIdentity?>
        {
            ["c"] = App("C", "B"),
            ["b"] = App("B", "A"),
            ["a"] = App("A"),
        };

        Assert.Equal(new[] { "a", "b", "c" }, Sort(new[] { "c", "b", "a" }, ids));
    }

    [Fact]
    public void MatchingFallsBackToNamePlusPublisher_WhenTheDependencyDeclaresNoAppId()
    {
        // DependencyRef carries Guid.Empty above on purpose: an app.json dependency entry that
        // names its target without an id must still be recognised, which is the same fallback
        // RunLayeredPrePass uses. Negative direction: a name that matches nothing does not order.
        var ids = new Dictionary<string, BundleIdentity?>
        {
            ["consumer"] = App("Consumer", "Nothing By This Name"),
            ["other"] = App("Other"),
        };

        Assert.Equal(new[] { "consumer", "other" }, Sort(new[] { "consumer", "other" }, ids));
    }

    // ── it moves nothing else ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDocumentedDependencyFirstOrder_IsReturnedByReference()
    {
        // Reference equality is the contract Program.cs reads to decide whether to reorder the
        // RESULTS as well: a request already in the documented order must pay nothing and must not
        // have its streamed test-line order perturbed. This caught a real defect — the first
        // version early-returned on "no edges at all", and the documented order HAS an edge (the
        // consumer's indegree is 1), so it built a new list and made every well-formed request pay
        // a result remap. The check is now on whether the sequence actually moved.
        var ids = new Dictionary<string, BundleIdentity?>
        {
            ["app"] = App("App"),
            ["test-app"] = App("Test App", "App"),
        };
        var input = new[] { "app", "test-app" };

        Assert.Same(input, Sort(input, ids));
    }

    [Fact]
    public void AWaitingBundle_IsEmittedWhenItsDependencyLands_NotSoonerToPreserveOrder()
    {
        // The precise disturbance guarantee, written down because the loose version ("unrelated
        // bundles keep their order") is FALSE and I asserted it before measuring. 'y' waits on 'z';
        // 'm' is unrelated to everything and becomes ready first, so it is emitted before both.
        // y and m have no relation and still swap.
        //
        // That is inherent: y cannot precede z, and deferring a ready node to preserve a relative
        // order that the dependency has already broken buys nothing. What IS guaranteed is that the
        // result is a deterministic function of the input, and that 'a' — ready from the start and
        // listed first — stays first.
        var ids = new Dictionary<string, BundleIdentity?>
        {
            ["a"] = App("A"),
            ["y"] = App("Y", "Z"),
            ["m"] = App("M"),
            ["z"] = App("Z"),
        };

        var sorted = Sort(new[] { "a", "y", "m", "z" }, ids);

        Assert.Equal(new[] { "a", "m", "z", "y" }, sorted);
        Assert.True(sorted.ToList().IndexOf("z") < sorted.ToList().IndexOf("y"),
            "the dependency must precede its consumer, which is the only ordering that was required");
    }

    [Fact]
    public void APathWithNoIdentity_HoldsItsPlaceAndOrdersNothing()
    {
        // A bundle with no readable app.json cannot depend on anything and cannot be depended
        // upon. It must not be treated as related to the other identity-less bundle either.
        var ids = new Dictionary<string, BundleIdentity?>
        {
            ["loose-1"] = null,
            ["loose-2"] = null,
            ["app"] = App("App"),
        };
        var input = new[] { "loose-1", "loose-2", "app" };

        Assert.Same(input, Sort(input, ids));
    }

    [Fact]
    public void FewerThanTwoIdentities_IsLeftAlone()
    {
        var ids = new Dictionary<string, BundleIdentity?> { ["only"] = App("Only"), ["loose"] = null };
        var input = new[] { "only", "loose" };

        Assert.Same(input, Sort(input, ids));
    }

    // ── it never loses or duplicates a bundle ──────────────────────────────────────────────────

    [Fact]
    public void ADependencyCycle_KeepsEveryBundle_InTheCallersOrder()
    {
        // Two bundles declaring each other. This sort does not get to adjudicate a cycle, and it
        // must not throw or drop one: Program.cs's ChangedLaterDependencyBundles guard still
        // applies to whatever ordering comes out, which is why that guard was kept rather than
        // deleted as #2614 suggested it could be.
        var ids = new Dictionary<string, BundleIdentity?>
        {
            ["p"] = App("P", "Q"),
            ["q"] = App("Q", "P"),
        };

        Assert.Equal(new[] { "p", "q" }, Sort(new[] { "p", "q" }, ids));
    }

    [Fact]
    public void ACycleAlongsideAnOrderablePair_StillOrdersTheOrderablePartAndKeepsTheRest()
    {
        // The drainable part is ordered; the cyclic remainder is appended in the caller's order.
        // Asserted as a set-plus-constraint rather than one exact sequence, because what matters
        // is that nothing is lost and the orderable relation is honoured.
        var ids = new Dictionary<string, BundleIdentity?>
        {
            ["consumer"] = App("Consumer", "Dep"),
            ["p"] = App("P", "Q"),
            ["q"] = App("Q", "P"),
            ["dep"] = App("Dep"),
        };

        var sorted = Sort(new[] { "consumer", "p", "q", "dep" }, ids).ToList();

        Assert.Equal(4, sorted.Count);
        Assert.Equal(new[] { "consumer", "dep", "p", "q" }.OrderBy(x => x), sorted.OrderBy(x => x));
        Assert.True(sorted.IndexOf("dep") < sorted.IndexOf("consumer"),
            "the orderable dependency must still precede its consumer despite an unrelated cycle in "
            + "the same request; got: " + string.Join(", ", sorted));
    }

    [Fact]
    public void ASingleBundle_IsReturnedByReference()
    {
        var input = new[] { "only" };
        Assert.Same(input, Sort(input, new Dictionary<string, BundleIdentity?>()));
    }
}
