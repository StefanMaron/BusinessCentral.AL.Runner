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

    /// <summary>
    /// Second and third unresolvable types. Three of them, not two, because the answer has to be
    /// distinguishable from the input array: with only two, half of all input orders already ARE
    /// the sorted order, so a tiebreak that just kept the input would pass half the permutations.
    /// Their ordinal order (AnotherNotAnAlObject, CodeunitHelpers, NotAnAlObject) is deliberately
    /// NOT this file's declaration order, which is what a tiebreak on Type.MetadataToken would
    /// follow. That is the only thing separating "the answer is a function of the types" from
    /// "the answer is the compiler's TypeDef layout", and the layout is what #2801 already cost
    /// a red CI leg.
    /// </summary>
    private sealed class NotAnAlObject { }

    private sealed class AnotherNotAnAlObject { }

    /// <summary>
    /// Two unresolvable types that share a SIMPLE name and differ only in their container. The
    /// key is <c>Type.FullName</c>, and this pair is what tells that apart from <c>Type.Name</c>:
    /// a Name-only key sees "Helper" twice, cannot separate them, and falls straight back to
    /// input order — the defect, reintroduced one level down. Not hypothetical for this method:
    /// BC's compiler-generated nested types are exactly the population the unresolved branch
    /// orders, and their simple names collide freely across containers.
    /// </summary>
    private sealed class AlphaContainer { internal sealed class Helper { } }

    private sealed class ZetaContainer { internal sealed class Helper { } }

    /// <summary>
    /// Differ only in the case of the final letter, which is what separates an ORDINAL
    /// comparison from a culture-aware one: ordinal puts 'I' (0x49) before 'i' (0x69), while
    /// every common culture — and the invariant culture — puts the lowercase letter first. The
    /// near-identical names are deliberate, not a typo.
    ///
    /// <para>The fact below asserts the ORDINAL answer, so it needs no
    /// <c>CultureInfo.CurrentCulture</c> override and cannot make the suite depend on the
    /// machine's locale: under globalization-invariant mode a culture-aware comparer degrades to
    /// ordinal and the fact still passes, it simply stops discriminating.</para>
    /// </summary>
    private sealed class FallbackI { }

    private sealed class Fallbacki { }

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

    /// <summary>
    /// Resolves to 62800 by the NAME shape, which is the id <see cref="Codeunit62899"/> resolves
    /// to by its attribute — so the two tie on the primary key. This is the only tie between
    /// RESOLVED types that the runner's own inputs can construct, and #3217's tiebreak decides it.
    /// </summary>
    private sealed class Codeunit62800 { }

    /// <summary>
    /// Runs the helper over every permutation of <paramref name="types"/> and asserts all of them
    /// produced <paramref name="expected"/> — the single assertion that makes the output a
    /// function of the type SET rather than of the array those types arrived in. Deliberately the
    /// same shape as <c>TestMethodOrderingContractTests.AssertOrderIsPermutationInvariant</c>:
    /// this is the same defect one level up, and it is checked the same way.
    /// </summary>
    private static void AssertOrderIsPermutationInvariant(Type[] types, string[] expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var permutationCount = 0;

        foreach (var permutation in Permutations(types))
        {
            permutationCount++;
            var ordered = TestExecutor.OrderTestCodeunitsByObjectId(permutation);
            seen.Add(string.Join(",", ordered.Select(Describe)));

            // Ordering may never change WHAT runs. A tiebreak that dropped or duplicated a type
            // could still be "deterministic" and satisfy the sequence check on its own.
            Assert.Equal(
                permutation.Select(Describe).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                ordered.Select(Describe).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        Assert.True(permutationCount > 1, "the sweep must cover more than one input order");
        Assert.True(seen.Count == 1,
            "codeunit order must be a function of the types, not of the Assembly.GetTypes() array "
            + $"they arrived in. {permutationCount} input permutations produced {seen.Count} "
            + "different orders:\n  " + string.Join("\n  ", seen.OrderBy(x => x, StringComparer.Ordinal)));
        Assert.Equal(string.Join(",", expected), seen.Single());
    }

    /// <summary>
    /// A label unique per TYPE rather than per simple name, with this class's own prefix dropped
    /// so a failure message stays readable. <c>Type.Name</c> will not do: AlphaContainer.Helper
    /// and ZetaContainer.Helper are two distinct types with one simple name, and describing them
    /// by it would hide a wrong answer from the sequence check AND a dropped type from the
    /// no-drop check.
    /// </summary>
    private static string Describe(Type t) =>
        (t.FullName ?? t.Name).Replace("AlRunner.Tests.TestCodeunitOrderingContractTests+", "");

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

    // ── the tiebreak among types the primary key cannot separate (#3217) ─────────────
    //
    // The primary key — ascending AL object id — is #2801's rule and is unchanged. What these
    // pin is the SECOND key, which used to be `.ThenBy(x => x.i)`: the type's index in the
    // Assembly.GetTypes() array. That is the same undefined reflection order #2801 existed to
    // get rid of, so the helper was total and deterministic for everything that resolved, and
    // undefined for everything that did not. Same defect, same fix and same proof shape as
    // #3201 one level down, in OrderTestMethodsBySourceDeclaration.

    /// <summary>
    /// TOTAL fallback: not one object id resolves. The old tiebreak returned these in whatever
    /// order they were handed over, so the answer was the CLR's to choose. They still sort after
    /// everything that resolved — that half is unchanged and is pinned by the mixed case below.
    /// </summary>
    [Fact]
    public void NoObjectIdResolves_OrderIsStillAFunctionOfTheTypes()
    {
        AssertOrderIsPermutationInvariant(
            new[] { typeof(NotAnAlObject), typeof(CodeunitHelpers), typeof(AnotherNotAnAlObject) },
            new[] { "AnotherNotAnAlObject", "CodeunitHelpers", "NotAnAlObject" });
    }

    /// <summary>
    /// PARTIAL fallback, and the "sorts last" half of the documented contract: every type whose
    /// id resolved leads, in ascending id order, and the unresolvable tail behind it is ordered
    /// by a key of its own rather than by the array position it arrived in.
    /// </summary>
    [Fact]
    public void UnresolvableTypes_SortLast_AndAmongThemselvesDeterministically()
    {
        AssertOrderIsPermutationInvariant(
            new[]
            {
                typeof(CodeunitHelpers), typeof(Codeunit62803),
                typeof(NotAnAlObject), typeof(Codeunit62801), typeof(AnotherNotAnAlObject),
            },
            new[]
            {
                "Codeunit62801", "Codeunit62803",
                "AnotherNotAnAlObject", "CodeunitHelpers", "NotAnAlObject",
            });
    }

    /// <summary>
    /// The third instance of the same shape, and the one that is reachable with ids that DO
    /// resolve: <see cref="Codeunit62800"/> resolves to 62800 by name and
    /// <see cref="Codeunit62899"/> resolves to 62800 by its <c>[ApplicationObjectId]</c>, so the
    /// primary key cannot separate them and the tiebreak decides which runs first.
    /// </summary>
    [Fact]
    public void TypesSharingOneObjectId_TieBreakDeterministically()
    {
        AssertOrderIsPermutationInvariant(
            new[] { typeof(Codeunit62899), typeof(Codeunit62800), typeof(Codeunit62801) },
            new[] { "Codeunit62800", "Codeunit62899", "Codeunit62801" });
    }

    /// <summary>
    /// The key is <c>Type.FullName</c>, not <c>Type.Name</c>. Both of these are called
    /// <c>Helper</c>, so a Name-only tiebreak compares them equal, keeps whichever order it was
    /// handed, and answers differently for the two input permutations.
    /// </summary>
    [Fact]
    public void TypesSharingASimpleName_AreSeparatedByTheirFullName()
    {
        AssertOrderIsPermutationInvariant(
            new[] { typeof(ZetaContainer.Helper), typeof(AlphaContainer.Helper) },
            new[] { "AlphaContainer+Helper", "ZetaContainer+Helper" });
    }

    /// <summary>
    /// The comparison is ORDINAL. A culture-aware comparer is deterministic too, so every other
    /// fact here passes against one — and it would put this pair the other way round on any
    /// machine whose culture data is loaded, which is the "holds across machines" half of the
    /// contract rather than a style preference.
    /// </summary>
    [Fact]
    public void TheUnresolvedTieBreak_IsOrdinal_NotCultureAware()
    {
        AssertOrderIsPermutationInvariant(
            new[] { typeof(Fallbacki), typeof(FallbackI) },
            new[] { "FallbackI", "Fallbacki" });
    }

    /// <summary>
    /// NEGATIVE CONTROL, and the reason the three facts above cannot be satisfied by deleting the
    /// id lookup and sorting by name. A resolved id outranks the tiebreak however the names
    /// compare: <c>Codeunit62899</c> carries attribute id 62800 and so must run FIRST, which is
    /// the exact reverse of its ordinal name order against <c>Codeunit62801</c>. Passes both
    /// before and after #3217 by design — it pins the #2801 rule the fix must leave alone.
    /// </summary>
    [Fact]
    public void ResolvedObjectIds_OutrankTheNameTieBreak()
    {
        AssertOrderIsPermutationInvariant(
            new[] { typeof(Codeunit62801), typeof(Codeunit62803), typeof(Codeunit62899) },
            new[] { "Codeunit62899", "Codeunit62801", "Codeunit62803" });
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
