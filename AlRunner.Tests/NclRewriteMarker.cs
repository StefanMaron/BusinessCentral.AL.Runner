// NclRewriteMarker — "has the Ncl on disk been Cecil-rewritten at all?" for binding tests
// that read the rewritten IL, and what to do when it has not. Issue #4782.
//
// The marker is metadata-level and prepend-independent: a rewritten Ncl holds MemberRefs into
// AlRunner.* types, a pristine one holds none. Keying on one specific prepend let that prepend's
// move turn whole classes into permanent green skips (#4142, #4782).
using Mono.Cecil;
using Xunit;

namespace AlRunner.Tests;

internal static class NclRewriteMarker
{
    /// <summary>True when <paramref name="module"/> references any member of an
    /// <c>AlRunner.*</c> type — which only a Cecil rewrite puts into Ncl.</summary>
    internal static bool IsRewritten(ModuleDefinition module)
        => module.GetMemberReferences().Any(r => IsRunnerType(r.DeclaringType));

    private static bool IsRunnerType(TypeReference? type)
    {
        while (type?.DeclaringType != null) type = type.DeclaringType;
        var ns = type?.Namespace;
        return ns != null && (ns == "AlRunner" || ns.StartsWith("AlRunner.", StringComparison.Ordinal));
    }

    internal static void SkipUnlessRewritten(ModuleDefinition module, string path)
        => SkipUnlessRewrittenIn(IsRewritten(module), path, TestArtifacts.RunningOnCi);

    /// <summary>
    /// Skip off CI; FAIL on CI, where bc-tests.yml warms the Cecil cache into the test bin
    /// before `dotnet test`, so an un-rewritten Ncl there is a broken rewrite step, not a
    /// legitimate skip — the same shape as <see cref="TestArtifacts.SkipIfMissingIn"/>.
    /// </summary>
    internal static void SkipUnlessRewrittenIn(bool rewritten, string path, bool runningOnCi)
    {
        if (rewritten) return;

        var reason = $"'{path}' has not been Cecil-rewritten (it references no AlRunner.* member), "
                   + "so there is nothing to assert about its prepends. Run "
                   + "tools/engine-test-bootstrap.sh after every build to warm it.";
        if (runningOnCi)
            Assert.Fail("The Ncl in the test bin is not Cecil-rewritten on a CI leg, where the "
                        + "'Warm the Ncl Cecil rewrite cache' step of bc-tests.yml guarantees it is — "
                        + "a skip here would report green having asserted nothing (#4782). " + reason);
        throw new SkipException(reason);
    }
}
