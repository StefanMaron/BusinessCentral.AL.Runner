// IntegerVirtualTableWindowGuardTests — issue #2350, with #3376 decided in the same pass.
//
// This is a RUNNER-MECHANISM test, not a claim about BC. The behavioural half — that a
// filter past the materialised window is refused by name on each of the four request paths,
// and that a filter inside it and an UNBOUNDED one are still served — is proven in AL by
// tests/runner-extras/integer-virtual-table-window (codeunit 64591), which is where a
// regression in the refusal itself shows up.
//
// What that AL suite cannot see is a guard that stops being REACHED. Three of the four hooks
// are installed by Cecil prepends in NclCecilRewrite.Runtime.cs, and CLAUDE.md records the
// failure mode directly: "an orphaned hook and a live one look identical" in the knowledge
// graph, because a registration that is deleted, renamed, or never added is not a compile
// error anywhere. It is exactly how #2350 came to exist — the file's header described an
// "IntegerWindowGuard" whose identifier appeared precisely once in the whole repository, in
// that comment, for a full release.
//
// So this test asserts the wiring: each guard helper exists with the signature
// PrependStaticCall emits (public static, (object, object)), and each is named by a
// registration in the rewrite source. Deleting a prepend line turns the corresponding AL
// test red only when the AL suite is run against a rebuilt runner; it turns this test red at
// once, and says which path went missing.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class IntegerVirtualTableWindowGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string RewriteSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "NclCecilRewrite.Runtime.cs"));

    private static string FindInterceptSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.FieldFindIntercept.cs"));

    // The three Cecil-prepended paths. The find path is wired differently — through the
    // DataAccess_IsManagedFindRequest predicate rather than a prepend of its own — and is
    // asserted separately below.
    [Theory]
    [InlineData("DataAccess_IntegerWindowGuardForCount")]
    [InlineData("DataAccess_IntegerWindowGuardForExists")]
    [InlineData("DataAccess_IntegerWindowGuardForGet")]
    public void EachGuardHelper_HasTheSignaturePrependStaticCallEmits(string helperName)
    {
        var m = typeof(RecordPatches).GetMethod(
            helperName, BindingFlags.Public | BindingFlags.Static);

        Assert.True(m != null,
            $"RecordPatches.{helperName} is missing or not public static. PrependStaticCall "
            + "resolves it by name at rewrite time, so a rename here is not a compile error — "
            + "it silently produces a runner with no guard on that path (issue #2350).");

        var ps = m!.GetParameters();
        Assert.True(ps.Length == 2 && ps.All(p => p.ParameterType == typeof(object)),
            $"RecordPatches.{helperName} must take (object self, object request) to match the "
            + "two argSlots the prepend pushes; got ("
            + string.Join(", ", ps.Select(p => p.ParameterType.Name)) + ").");
        Assert.Equal(typeof(void), m.ReturnType);
    }

    [Theory]
    [InlineData("DataAccess_IntegerWindowGuardForCount", "CountAsync", "CountCacheRequest")]
    [InlineData("DataAccess_IntegerWindowGuardForExists", "ExistsAsync", "ExistsCacheRequest")]
    [InlineData("DataAccess_IntegerWindowGuardForGet", "InternalTryGetByPrimaryKeyAsync", "PrimaryKeyCacheRequest")]
    public void EachGuard_IsRegisteredAgainstItsOwnDataAccessEntryPoint(
        string helperName, string method, string requestType)
    {
        var src = RewriteSource;

        Assert.True(src.Contains(helperName, StringComparison.Ordinal),
            $"No Cecil registration names {helperName}. Record.Count(), Record.IsEmpty() and "
            + "Record.Get() reach three DIFFERENT DataAccess entry points, so a guard missing "
            + "from any one of them answers that path from the window silently — the shape "
            + "#3006 found for Date after the count guard's comment had claimed to cover "
            + "IsEmpty() for a whole release.");

        // The registration must sit against the right entry point, not merely exist. A guard
        // pointed at the wrong method compiles, rewrites cleanly, and never fires for the path
        // it was written for.
        var at = src.IndexOf(helperName, StringComparison.Ordinal);
        var window = src.Substring(Math.Max(0, at - 400), Math.Min(500, src.Length - Math.Max(0, at - 400)));
        Assert.True(window.Contains(method, StringComparison.Ordinal),
            $"{helperName} is registered, but not against DataAccess.{method}.");
        Assert.True(window.Contains(requestType, StringComparison.Ordinal),
            $"{helperName} is registered, but not against a {requestType}.");
    }

    [Fact]
    public void FindPath_IsGuardedThroughTheManagedFindPredicate()
    {
        // The find path has no prepend of its own: DataAccess.InnerFindAsync already carries
        // the Field-table branch, and Date and Aggregate Permission Set take their side effects
        // inside that same predicate. Integer joins them there rather than adding a fourth
        // prepend to one method.
        var src = FindInterceptSource;

        var at = src.IndexOf("DataAccess_IsManagedFindRequest", StringComparison.Ordinal);
        Assert.True(at >= 0, "DataAccess_IsManagedFindRequest is gone from the find intercept.");

        var body = src.Substring(at);
        Assert.True(body.Contains("EnsureIntegerWindowCoversRequest", StringComparison.Ordinal),
            "The managed-find predicate no longer calls EnsureIntegerWindowCoversRequest, so a "
            + "FindSet() past the window is served from the window again — the original #2350 "
            + "defect, on the path a report dataitem actually takes.");
    }

    [Fact]
    public void TheWindowIsInclusiveAtBothEdges_AndTheRefusalNamesBoundAndWindow()
    {
        // Pins the two constants the refusal quotes, so a change to either has to be
        // deliberate. -1000 is the edge the AL suite's below-the-window test rests on, and
        // it is the one a guard written only against IntegerWindowMax would leave open.
        Assert.Equal(-1000, RecordPatches.IntegerWindowMinDefault);
        Assert.Equal(100000, RecordPatches.IntegerWindowMaxDefault);

        // Real BC clamps rather than refuses, at bounds three orders of magnitude wider:
        // IntegerDataProvider.GetValuesWithinRangeForKeyField and CountValuesWithinRange both
        // call GetInclusiveIntegerBounds(-1000000000, 1000000000, ...) — decompiled from
        // Ncl.dll, byte-identical in 27.0 and 28.4. So the window is a runner limit, and the
        // refusal must say so rather than reading as a statement about BC.
        Assert.True(RecordPatches.IntegerWindowMaxDefault < 1_000_000_000,
            "The materialised window must stay inside the range a service tier serves; past "
            + "that the refusal would be claiming BC cannot answer something it can.");
    }

    [Fact]
    public void TheRefusalIsAnchoredNotYetImplemented_SoATryFunctionCannotSwallowIt()
    {
        // Issue #2965's rule. ApplicationObjectBasePatches.IsPermanentOutOfScope reads the
        // reason's FIRST token; anchored anywhere else, an AL [TryFunction] traps the refusal
        // into `false` and the caller carries on having quietly done without the table — the
        // silent default .claude/rules/loud-failures.md exists to prevent. The AL suite pins
        // the runtime consequence; this pins the anchor the whole mechanism turns on.
        var gap = RecordPatches.IntegerShapeGap("probe detail");

        Assert.StartsWith("not-yet-implemented", gap.Reason, StringComparison.Ordinal);
        Assert.Contains("integer-virtual-table", gap.Reason, StringComparison.Ordinal);
        Assert.Contains("Integer (virtual table 2000000026)", gap.Message, StringComparison.Ordinal);

        // docs/scope.md is the permanently-out-of-scope manifest and names no table; citing it
        // here would tell the next reader the surface will never work (#2945).
        Assert.DoesNotContain("docs/scope.md", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothWindowEdges_AreOverridable_SoTheRefusalsAdviceCanBeFollowed()
    {
        // The lower edge was a hard `const` while the upper one read an environment variable.
        // Harmless while nothing compared a request against either edge; a dead end as soon as
        // the guard began refusing, because the refusal advised raising
        // AL_RUNNER_INTEGER_WINDOW_MAX for a bound the MIN edge had rejected — advice that
        // cannot work. Measured against the corpus tests on the upstream branch: with
        // AL_RUNNER_INTEGER_WINDOW_MAX=300000 the far-above test passed and the far-below one
        // still failed; with both variables set, all seven passed.
        //
        // Asserting the property rather than the plumbing: an override must be able to widen
        // each edge past the bound the corpus tests name.
        var minProp = typeof(RecordPatches).GetProperty("IntegerWindowMin",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        var maxProp = typeof(RecordPatches).GetProperty("IntegerWindowMax",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        Assert.True(minProp != null,
            "IntegerWindowMin must be a property reading AL_RUNNER_INTEGER_WINDOW_MIN, not a "
            + "const — otherwise the refusal for a below-window bound names a variable that "
            + "cannot widen the edge that refused it.");
        Assert.True(maxProp != null, "IntegerWindowMax must stay overridable.");
    }
}
