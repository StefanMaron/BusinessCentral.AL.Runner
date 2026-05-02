using System.Reflection;
using AlRunner.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for <see cref="MockCodeunitHandle.ScoreMethodMatch"/> — issue #1577.
///
/// Before the fix, six 2-param overloads on the auto-stubbed "No. Series" codeunit all
/// scored 11, causing AreRelated(NavCode, NavCode) to win over GetNextNo(MockVariant, NavDate)
/// by reflection-enumeration order. The fix introduces tiered scoring:
///   +1000 exact type match
///   +100  IsAssignableFrom (inherited)
///   +10   HasKnownConversion (Variant wrap, ByRef wrap, etc.)
///   +5    object param
///   +0    no known conversion path
///
/// These tests verify the discriminating tier transitions directly, without needing
/// alc.exe or a real .app package.
/// </summary>
public class MockCodeunitHandleScoreTests
{
    // ── Fixture classes ──────────────────────────────────────────────────────────
    // These inner methods exist only to be reflected — their bodies are never called.

    private static class TwoArgFixture
    {
        // Simulates AreRelated(NavCode, NavCode) — the WRONG candidate for (NavCode, NavDate) args.
        public static void WrongCandidate(NavCode p1, NavCode p2) => throw new NotImplementedException();

        // Simulates GetNextNo_<id>(MockVariant, NavDate) — the RIGHT candidate.
        public static void RightCandidate(MockVariant p1, NavDate p2) => throw new NotImplementedException();

        // Simulates GetNextNo(NavCode, NavDate) — exact match on both params.
        public static void ExactMatch(NavCode p1, NavDate p2) => throw new NotImplementedException();

        // Simulates LookupRelatedNoSeries(NavCode, ByRef<NavCode>) — ByRef second param.
        public static void ByRefCandidate(NavCode p1, ByRef<NavCode> p2) => throw new NotImplementedException();
    }

    private static class OneArgFixture
    {
        // NavCode param — exact match.
        public static void NavCodeParam(NavCode p1) => throw new NotImplementedException();

        // NavDate param — NO known conversion from NavCode.
        public static void NavDateParam(NavDate p1) => throw new NotImplementedException();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static MethodInfo GetMethod(Type fixture, string name)
        => fixture.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
           ?? throw new InvalidOperationException($"Method {name} not found on {fixture.Name}");

    private static NavCode MakeNavCode(string value = "TEST")
        => new NavCode(20, value);

    private static NavDate MakeNavDate()
        // Use NavDate.Default — the value doesn't matter for type-based scoring.
        => NavDate.Default;

    // ── Tests ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core regression for #1577: args (NavCode, NavDate) must score
    /// (MockVariant, NavDate) HIGHER than (NavCode, NavCode).
    ///
    /// Old scoring: both tied at 11.
    /// New scoring:
    ///   WrongCandidate: NavCode==NavCode → +1000; NavDate vs NavCode → +0 → total 1000.
    ///   RightCandidate: NavCode vs MockVariant → +10 (known wrap); NavDate==NavDate → +1000 → total 1010.
    /// </summary>
    [Fact]
    public void ScoreMethodMatch_PrefersExactSecondParamOverFirstParamCoercion()
    {
        var args = new object[] { MakeNavCode(), MakeNavDate() };

        var wrongMethod = GetMethod(typeof(TwoArgFixture), nameof(TwoArgFixture.WrongCandidate));
        var rightMethod = GetMethod(typeof(TwoArgFixture), nameof(TwoArgFixture.RightCandidate));

        var wrongScore = MockCodeunitHandle.ScoreMethodMatch(wrongMethod, args);
        var rightScore = MockCodeunitHandle.ScoreMethodMatch(rightMethod, args);

        Assert.True(rightScore > wrongScore,
            $"(MockVariant, NavDate) must score higher than (NavCode, NavCode) for args (NavCode, NavDate). " +
            $"WrongScore={wrongScore}, RightScore={rightScore}");
    }

    /// <summary>
    /// Exact match on ALL params beats a candidate that wraps only the first param
    /// into MockVariant.
    ///
    /// ExactMatch (NavCode, NavDate): +1000 + 1000 = 2000.
    /// RightCandidate (MockVariant, NavDate): +10 + 1000 = 1010.
    /// </summary>
    [Fact]
    public void ScoreMethodMatch_ExactMatchBeatsKnownConversion()
    {
        var args = new object[] { MakeNavCode(), MakeNavDate() };

        var exactMethod = GetMethod(typeof(TwoArgFixture), nameof(TwoArgFixture.ExactMatch));
        var wrapMethod = GetMethod(typeof(TwoArgFixture), nameof(TwoArgFixture.RightCandidate));

        var exactScore = MockCodeunitHandle.ScoreMethodMatch(exactMethod, args);
        var wrapScore = MockCodeunitHandle.ScoreMethodMatch(wrapMethod, args);

        Assert.True(exactScore > wrapScore,
            $"Exact match on all params must score higher than Variant-wrap. " +
            $"ExactScore={exactScore}, WrapScore={wrapScore}");
    }

    /// <summary>
    /// No known conversion from NavCode to NavDate → score 0 (not the old +1 "may be convertible").
    ///
    /// This ensures a param mismatch with no known path contributes 0, not a positive value
    /// that could still win a tie.
    /// </summary>
    [Fact]
    public void ScoreMethodMatch_NoKnownConversionScoresZero()
    {
        var args = new object[] { MakeNavCode() };

        var navDateParamMethod = GetMethod(typeof(OneArgFixture), nameof(OneArgFixture.NavDateParam));

        var score = MockCodeunitHandle.ScoreMethodMatch(navDateParamMethod, args);

        Assert.Equal(0, score);
    }

    /// <summary>
    /// ByRef wrap (ByRef&lt;NavCode&gt; param vs NavDate arg) must score LOWER than
    /// direct MockVariant wrap, so the non-ByRef candidate wins.
    ///
    /// For args (NavCode, NavDate):
    ///   RightCandidate (MockVariant, NavDate): +10 + 1000 = 1010.
    ///   ByRefCandidate (NavCode, ByRef&lt;NavCode&gt;): +1000 + ? where ? &lt; 10 → total &lt; 1010.
    ///
    /// This prevents the ByRef-wrapping path from tying or beating the direct-Variant path,
    /// which would expose a subsequent NavDate→NavCode conversion error inside the ByRef.
    /// </summary>
    [Fact]
    public void ScoreMethodMatch_ByRefWrapScoresLowerThanDirectVariantWrap()
    {
        var args = new object[] { MakeNavCode(), MakeNavDate() };

        var rightMethod = GetMethod(typeof(TwoArgFixture), nameof(TwoArgFixture.RightCandidate));
        var byRefMethod = GetMethod(typeof(TwoArgFixture), nameof(TwoArgFixture.ByRefCandidate));

        var rightScore = MockCodeunitHandle.ScoreMethodMatch(rightMethod, args);
        var byRefScore = MockCodeunitHandle.ScoreMethodMatch(byRefMethod, args);

        Assert.True(rightScore > byRefScore,
            $"Direct Variant wrap (MockVariant, NavDate) must score strictly higher than ByRef wrap (NavCode, ByRef<NavCode>) " +
            $"for args (NavCode, NavDate). RightScore={rightScore}, ByRefScore={byRefScore}");
    }
}
