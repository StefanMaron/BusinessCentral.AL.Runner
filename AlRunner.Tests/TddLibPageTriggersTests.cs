// --tdd and the code of a page that a TestPage runs (#5309), across source bundles: `al-runner --tdd lib test`.
// The library declares a page with triggers and actions and a helper that opens a TestPage on it; the test bundle
// drives TestPages of that page, extends it with a page extension, and subscribes to its close event. The stubs
// are called by page code in the library (compiled before the test bundle), or by code the test bundle adds to
// the library's page, so what a TestPage call is recorded as must cross the bundles the way a table operation's
// does (#5286, #5301).
//
// Runner-specific (the --tdd call graph across the runner's own compiles), so it lives here, not in the
// al-language corpus: the claim is about the generator, not about BC. A class of its own: TddLibTestPageTests
// is already the slowest of the family.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddLibPageTriggersTests
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibPageTriggers");

    private static readonly string[] InOrder = { Path.Combine(Root, "lib"), Path.Combine(Root, "test") };
    private static readonly string[] Reversed = { Path.Combine(Root, "test"), Path.Combine(Root, "lib") };

    private static string LibStub(string member) => $"TPG Lib Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private static string TestStub(string member) => $"TPGT Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private static void AssertStubs(TddRunResult run, string test, params string[] stubs)
    {
        Assert.Equal("pass", run.Find(test).GetProperty("status").GetString());
        Assert.Equal(stubs.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            run.StubsOf(test).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Every assertion of the fixture, for a run in either listing order and on any cache.</summary>
    internal static void AssertAnnotated(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 0, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(6, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));

        // The page and its OnOpenPage are the library's, the row trigger is the test bundle's page extension's.
        AssertStubs(run, "OpenView_StartsTheLibrarysOnOpenPageAndTheExtensionsRowTrigger",
            LibStub("MLibOpen"), TestStub("MTestExtAfterGet"));
        // One of the library's two actions: the other's OnAction is not started.
        AssertStubs(run, "Invoke_OnALibraryAction_StartsThatActionsOnActionOnly",
            LibStub("MLibOpen"), TestStub("MTestExtAfterGet"), LibStub("MLibAction"));
        // A control the test bundle's page extension adds: its trigger, and not the library's own field's.
        AssertStubs(run, "SetValue_OnAControlOfThePageExtension_StartsTheExtensionsFieldTrigger",
            LibStub("MLibOpen"), TestStub("MTestExtAfterGet"), TestStub("MTestExtValidate"));
        // A subscriber, in the test bundle, of the library page's close event.
        AssertStubs(run, "Close_StartsTheSubscriberOfThePagesCloseEventInThisBundle",
            LibStub("MLibOpen"), TestStub("MTestExtAfterGet"), TestStub("MTestEvtClose"));
        // The TestPage call is in a procedure of the library, compiled before the test bundle's extension and
        // subscriber: the edge it leaves is what connects the test calling that procedure to their stubs.
        AssertStubs(run, "LibraryHelperOpeningThePage_StartsWhatThisBundleAddsToIt",
            LibStub("MLibOpen"), TestStub("MTestExtAfterGet"), TestStub("MTestEvtClose"));
        AssertStubs(run, "NoPage_IsNotAnnotated");
    }

    /// <summary>The library first, then the test bundle.</summary>
    [SkippableFact]
    public void PageCodeIsFollowedAcrossBundles()
    {
        TestArtifacts.SkipIfMissing();
        using var run = new TddRunResult(InOrder);
        AssertAnnotated(run, "lib test");
        Assert.Contains("--tdd: generated 7 member(s) this run:", run.StdErr);
    }

    /// <summary>
    /// ONE cache, a plain run first: it compiles the library clean and caches it with no edges, so a --tdd run on
    /// the same cache must compile it again to leave them (a source dependency with none is never served from the
    /// cache under --tdd); a second --tdd run does the same, and a plain run after them still refuses the
    /// library's missing members instead of being served a stub. Either listing order.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlainThenTddThenTddThenPlainOnOneCache_AnnotatesEveryTddRunAndNeverServesAStubToAPlainOne(bool testBundleFirst)
    {
        TestArtifacts.SkipIfMissing();
        var folders = testBundleFirst ? Reversed : InOrder;
        var cache = TestScratch.Dir("al-runner-tdd-pagetriggers-cache-" + (testBundleFirst ? "rev" : "ord"));
        Directory.CreateDirectory(cache);
        try
        {
            using var plain = new TddRunResult(folders) { CacheRoot = cache, Plain = true };
            Assert.NotEqual(0, plain.Exit);
            using var first = new TddRunResult(folders) { CacheRoot = cache };
            AssertAnnotated(first, "tdd after a plain run");
            using var second = new TddRunResult(folders) { CacheRoot = cache };
            AssertAnnotated(second, "tdd after a tdd run");
            using var after = new TddRunResult(folders) { CacheRoot = cache, Plain = true };
            Assert.NotEqual(0, after.Exit);
            Assert.DoesNotContain(after.Tests, t => t.GetProperty("status").GetString() == "pass");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
