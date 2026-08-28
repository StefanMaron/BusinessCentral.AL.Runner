// DependencyLoaderServiceTierFallbackTests — pins DependencyLoader.HasFaithfulServiceTierFallback
// (issue #2131).
//
// DependencyLoader.LoadAll USED TO swallow ANY Tier-3 compile failure for a Microsoft
// "source-only" dependency (bare `when (microsoftSourceOnly)`) and silently defer to
// "service-tier DLL dispatch" — even when no such dispatch existed for that app at all.
// That is exactly how Microsoft's real "Library Assert" test-library app (ships full AL
// source, zero service-tier DLL coverage) turned an EMIT-ZERO compile failure into a
// swallowed no-op: the dependency silently failed to load, and the FIRST test that called
// one of its members died with a cryptic `NavNCLMissingMethodException` ("object with ID 0
// does not have a member with that ID") instead of ever seeing the real cause.
//
// This pins the DECISION the fix narrows that catch guard to, in isolation from the full
// BC engine / Emit / Assembly pipeline: swallowing is faithful ONLY when the extracted
// service-tier DLL index genuinely covers at least one of the failing app's own codeunits.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyLoaderServiceTierFallbackTests
{
    [Fact]
    public void HasFaithfulServiceTierFallback_FalseWhenIndexUnavailable_EvenIfNamesWouldMatch()
    {
        // The exact "Library Assert" shape on a machine/CI leg with no extracted
        // service-tier DLL cache at all (ServiceTierDllIndex.Available == false, which is
        // the CI default — no extraction step runs there): even a codeunit name the index
        // WOULD serve if it were available must not count as a fallback here — there is
        // no real DLL to dispatch to.
        var result = DependencyLoader.HasFaithfulServiceTierFallback(
            serviceTierIndexAvailable: false,
            codeunitTypeNames: new[] { "Codeunit130002" },
            indexContains: _ => true);

        Assert.False(result);
    }

    [Fact]
    public void HasFaithfulServiceTierFallback_FalseWhenAppHasNoCodeunits()
    {
        // A source-only app that declares zero codeunits (e.g. only tables/pages) has
        // nothing for the index to cover — vacuously no fallback, not vacuously "safe to
        // swallow".
        var result = DependencyLoader.HasFaithfulServiceTierFallback(
            serviceTierIndexAvailable: true,
            codeunitTypeNames: Array.Empty<string>(),
            indexContains: _ => true);

        Assert.False(result);
    }

    [Fact]
    public void HasFaithfulServiceTierFallback_FalseForLibraryAssertShapedApp_ZeroCoverage()
    {
        // The actual reported case (#2131): Microsoft "Library Assert" ships ONE codeunit
        // (130002) whose body exists only as AL source — it was never precompiled into any
        // service-tier DLL. Even with the index available, THIS specific codeunit is not
        // in it. This must return false so DependencyLoader.LoadAll's catch guard does NOT
        // swallow the failure — the caller needs to see the real DependencyLoadException.
        var result = DependencyLoader.HasFaithfulServiceTierFallback(
            serviceTierIndexAvailable: true,
            codeunitTypeNames: new[] { "Codeunit130002" },
            indexContains: name => name is "Codeunit1" or "Codeunit9015"); // unrelated platform codeunits

        Assert.False(result);
    }

    [Fact]
    public void HasFaithfulServiceTierFallback_TrueWhenAtLeastOneCodeunitIsCovered()
    {
        // Case (a) from the tier comment: a platform-runtime-shaped app not yet in
        // KnownPlatformRuntimeApps, where the index DOES cover (at least some of) its
        // codeunits — a genuine "index gap", not a total absence. Swallowing here is
        // faithful: the real MS-compiled body still runs via lazy dispatch.
        var result = DependencyLoader.HasFaithfulServiceTierFallback(
            serviceTierIndexAvailable: true,
            codeunitTypeNames: new[] { "Codeunit1", "Codeunit2", "Codeunit3" },
            indexContains: name => name == "Codeunit2");

        Assert.True(result);
    }
}
