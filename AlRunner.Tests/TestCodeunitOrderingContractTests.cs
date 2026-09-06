// TestCodeunitOrderingContractTests — the no-op-proof half of #2801's ordering fix (#3086).
//
// WHY THIS FILE EXISTS. #3082 made TestExecutor order test codeunits by ascending AL object
// id instead of walking Assembly.GetTypes() in whatever order the CLR handed back, and
// guarded it with two end-to-end suites — TestCodeunitExecutionOrderTests and
// AlObjectEmitOrderDeterminismTests. Both spawn the runner over an AL fixture and read the
// order back out of its printed output, so what they can observe is
//
//     (the AL compiler's TypeDef layout on this machine, today)  ->  the sort  ->  the output
//
// and the first term is exactly the thing #2801 established is not stable. They therefore
// detect a broken sort only when the layout happens to disagree with ascending id.
//
// Measured, on this repo at be7a4de0, with OrderTestCodeunitsByObjectId reduced to
// `types => types`:
//
//     AlObjectEmitOrderDeterminismTests          1 of 1 FAILED
//     TestCodeunitExecutionOrderTests            3 of 4 FAILED
//     SuiteAbortOnTimeoutTests                   0 of 7 failed — ALL SEVEN PASSED
//
// The suite whose flake motivated the fix does not notice the fix being removed. It was
// green because GetTypes() happened to return its two-codeunit fixture in ascending id
// order on this machine; on the CI leg in issue #3086 it returned the other one, and the
// same six tests went red. A guard that swings with the thing under test is not a guard.
//
// WHAT THIS FILE DOES INSTEAD. It calls the ordering helper directly with an input array it
// controls, so "whatever order GetTypes() returned" is a variable of the test rather than a
// condition of the machine. Every fact below fails outright against `types => types`,
// deterministically, on any machine and any BC version — and needs no BC artifact, no
// subprocess and no AL compile, so it runs in milliseconds rather than the ~60s the
// end-to-end pair costs.
//
// It does NOT replace those suites. They prove the whole chain (AL source -> emit -> load ->
// execute) really is wired to this rule; this proves the rule itself is a rule.
using System.Reflection;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestCodeunitOrderingContractTests
{
    // ── Fixture types ────────────────────────────────────────────────────────────────
    //
    // TestExecutor.TryReadAlObjectId resolves an id from BC's [ApplicationObjectId]
    // attribute first and falls back to the `Codeunit<digits>` type-name shape. Plain C#
    // classes named that way exercise the fallback exactly as an emitted AL codeunit does,
    // because the fallback reads nothing but Type.Name.

    private sealed class Codeunit62801 { }
    private sealed class Codeunit62802 { }
    private sealed class Codeunit62803 { }

    /// <summary>Not an AL object at all — no attribute, and the name's suffix is not digits.</summary>
    private sealed class CodeunitHelpers { }

    /// <summary>Second unresolvable type, so "keeps their relative input order" is testable.</summary>
    private sealed class NotAnAlObject { }

    /// <summary>
    /// Stand-in for BC's <c>Microsoft.Dynamics.Nav.Runtime.ApplicationObjectIdAttribute</c>.
    /// TryReadAlObjectId matches it by <c>Type.Name</c> and reads
    /// <c>ApplicationObjectId.ObjectNumber</c> reflectively, so the shape is the contract and
    /// the namespace is not.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    private sealed class ApplicationObjectIdAttribute : Attribute
    {
        public ApplicationObjectIdAttribute(int objectNumber) => ApplicationObjectId = new Aoid(objectNumber);
        public Aoid ApplicationObjectId { get; }

        internal sealed class Aoid
        {
            public Aoid(int objectNumber) => ObjectNumber = objectNumber;
            public int ObjectNumber { get; }
        }
    }

    /// <summary>
    /// Name says 62899, attribute says 62800. The attribute is documented to win, and nothing
    /// tested that before: an implementation that dropped the attribute branch and kept only
    /// the name fallback passed every existing test, because in real AL output the two always
    /// agree.
    /// </summary>
    [ApplicationObjectId(62800)]
    private sealed class Codeunit62899 { }

    private static int[] IdsOf(IEnumerable<Type> types) =>
        types.Select(t => t.Name.StartsWith("Codeunit", StringComparison.Ordinal)
                          && int.TryParse(t.Name.AsSpan("Codeunit".Length), out var n) ? n : -1)
             .ToArray();

    // ── The rule ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Positive, and the one that fails hardest against a no-op: fed strictly DESCENDING, the
    /// helper must return strictly ASCENDING. `types => types` returns the input unchanged and
    /// fails here on every machine, which is precisely what the end-to-end guards cannot
    /// promise.
    /// </summary>
    [Fact]
    public void DescendingInput_ComesBackAscending()
    {
        var ordered = TestExecutor.OrderTestCodeunitsByObjectId(
            new[] { typeof(Codeunit62803), typeof(Codeunit62802), typeof(Codeunit62801) });

        Assert.Equal(new[] { 62801, 62802, 62803 }, IdsOf(ordered));
    }

    /// <summary>
    /// The rule stated as a rule: EVERY one of the six input permutations of three codeunits
    /// must produce the same single answer. This is the property `Assembly.GetTypes()` denies
    /// the caller and the reason the helper exists — asserted exhaustively, so it cannot hold
    /// only for the arrangement one compiler happened to emit.
    /// </summary>
    [Fact]
    public void EveryInputPermutation_ProducesTheSameAscendingOrder()
    {
        var all = new[] { typeof(Codeunit62801), typeof(Codeunit62802), typeof(Codeunit62803) };
        var expected = new[] { 62801, 62802, 62803 };
        var seen = 0;

        foreach (var permutation in Permutations(all))
        {
            seen++;
            Assert.Equal(expected, IdsOf(TestExecutor.OrderTestCodeunitsByObjectId(permutation)));
        }

        // 3! — proves the loop actually ran the whole space rather than zero or one case.
        Assert.Equal(6, seen);
    }

    /// <summary>
    /// The documented "stable, and total" half. A type whose object id cannot be read sorts
    /// AFTER everything that resolved, and two such types keep their relative input order —
    /// so feeding the pair both ways round is the only way to tell "stable" from "happens to
    /// come out that way".
    /// </summary>
    [Fact]
    public void UnresolvableTypes_SortLast_AndKeepTheirRelativeInputOrder()
    {
        var helpersFirst = TestExecutor.OrderTestCodeunitsByObjectId(
            new[] { typeof(CodeunitHelpers), typeof(Codeunit62803), typeof(NotAnAlObject), typeof(Codeunit62801) });
        Assert.Equal(
            new[] { typeof(Codeunit62801), typeof(Codeunit62803), typeof(CodeunitHelpers), typeof(NotAnAlObject) },
            helpersFirst);

        var helpersSecond = TestExecutor.OrderTestCodeunitsByObjectId(
            new[] { typeof(NotAnAlObject), typeof(Codeunit62803), typeof(CodeunitHelpers), typeof(Codeunit62801) });
        Assert.Equal(
            new[] { typeof(Codeunit62801), typeof(Codeunit62803), typeof(NotAnAlObject), typeof(CodeunitHelpers) },
            helpersSecond);
    }

    /// <summary>
    /// Negative: a class called <c>CodeunitHelpers</c> must not be read as object 0 and sorted
    /// to the FRONT of the run. It is the one failure mode a sloppier "strip the prefix, parse
    /// what is left" implementation produces, and it would put a non-test type ahead of every
    /// real codeunit.
    /// </summary>
    [Fact]
    public void NonNumericCodeunitPrefix_IsNotReadAsObjectZero()
    {
        var ordered = TestExecutor.OrderTestCodeunitsByObjectId(
            new[] { typeof(CodeunitHelpers), typeof(Codeunit62801) });

        Assert.Equal(typeof(Codeunit62801), ordered[0]);
    }

    /// <summary>
    /// The [ApplicationObjectId] branch, and its precedence over the name shape: a type NAMED
    /// Codeunit62899 but carrying an attribute id of 62800 sorts as 62800 — i.e. FIRST, ahead
    /// of 62801, which it could not do if only the name were read.
    /// </summary>
    [Fact]
    public void ApplicationObjectIdAttribute_WinsOverTheTypeNameShape()
    {
        var ordered = TestExecutor.OrderTestCodeunitsByObjectId(
            new[] { typeof(Codeunit62801), typeof(Codeunit62803), typeof(Codeunit62899) });

        Assert.Equal(
            new[] { typeof(Codeunit62899), typeof(Codeunit62801), typeof(Codeunit62803) },
            ordered);
    }

    /// <summary>
    /// Nothing is added and nothing is dropped. A sort that silently loses a type would hide
    /// whole codeunits from the run — the same class of silent test loss #2415 was filed for —
    /// and every assertion above would still pass while it happened.
    /// </summary>
    [Fact]
    public void OrderingIsAPermutation_NoTypeAddedOrDropped()
    {
        var input = new[]
        {
            typeof(Codeunit62803), typeof(CodeunitHelpers), typeof(Codeunit62899),
            typeof(Codeunit62801), typeof(NotAnAlObject), typeof(Codeunit62802),
        };

        var ordered = TestExecutor.OrderTestCodeunitsByObjectId(input);

        Assert.Equal(input.Length, ordered.Length);
        Assert.Equal(input.OrderBy(t => t.Name, StringComparer.Ordinal),
                     ordered.OrderBy(t => t.Name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Degenerate inputs the helper is handed on any bundle with no test codeunits at all —
    /// pinned so a future rewrite cannot throw on them, which would take down the whole run.
    /// </summary>
    [Fact]
    public void EmptyAndSingletonInputs_AreHandled()
    {
        Assert.Empty(TestExecutor.OrderTestCodeunitsByObjectId(Array.Empty<Type>()));
        Assert.Equal(new[] { typeof(Codeunit62802) },
                     TestExecutor.OrderTestCodeunitsByObjectId(new[] { typeof(Codeunit62802) }));
    }

    private static IEnumerable<Type[]> Permutations(Type[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest))
                yield return new[] { items[i] }.Concat(tail).ToArray();
        }
    }
}
