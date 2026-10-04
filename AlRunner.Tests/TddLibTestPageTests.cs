// --tdd and what a TestPage runs on its source table (#5301), across source bundles: `al-runner --tdd lib test`.
// The library declares a table with triggers, its page, and a helper that opens a TestPage on it; the test
// bundle drives TestPages of the page and extends the table with a trigger of its own. The stubs are called by
// triggers in a bundle compiled before the test, or by one compiled after the library's helper, so the raise a
// TestPage call is recorded as must cross the bundles the way a table operation's does (#5286).
//
// Runner-specific (the --tdd call graph across the runner's own compiles), so it lives here, not in the
// al-language corpus: the claim is about the generator, not about BC.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddLibTestPageTests
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibTestPage");

    private static readonly string[] InOrder = { Path.Combine(Root, "lib"), Path.Combine(Root, "test") };
    private static readonly string[] Reversed = { Path.Combine(Root, "test"), Path.Combine(Root, "lib") };

    private static string LibStub(string member) => $"TPL Lib Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private static string TestStub(string member) => $"TPLT Target: procedure \"{member}\"(Arg1: Integer): Integer";

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
        Assert.Equal(5, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));

        // The trigger and its stub are the library's, the page too: the test bundle's TestPage names both
        // the library's trigger and the one its own table extension adds.
        AssertStubs(run, "OpenNew_StartsTheInsertOfTheLibrarysTable", LibStub("MLibInsert"), TestStub("MTestExtInsert"));
        AssertStubs(run, "SetValue_StartsTheValidateAndInsertOfTheLibrarysTable",
            LibStub("MLibValidate"), LibStub("MLibExtValidate"), LibStub("MLibInsert"), TestStub("MTestExtInsert"));
        // The control is a page extension's, bound to a table extension's field, both in the library: the table is
        // the extension's base table, so the library's other table is not named.
        AssertStubs(run, "SetValue_OnAControlOfALibrarysPageExtension_StartsTheBaseTablesTriggersOnly",
            LibStub("MLibValidate"), LibStub("MLibExtValidate"), LibStub("MLibInsert"), TestStub("MTestExtInsert"));
        // The TestPage call is in a procedure of the library, compiled before the test bundle's table extension:
        // the edge it leaves is what connects the test calling that procedure to the extension's stub.
        AssertStubs(run, "LibraryHelperOpeningANewRecord_StartsTheInsertOfTheTableExtensionInThisBundle",
            LibStub("MLibInsert"), TestStub("MTestExtInsert"));
        AssertStubs(run, "OpenEditAndRead_IsNotAnnotated");
    }

    /// <summary>The library first, then the test bundle.</summary>
    [SkippableFact]
    public void TestPageCallsAreFollowedAcrossBundles()
    {
        TestArtifacts.SkipIfMissing();
        using var run = new TddRunResult(InOrder);
        AssertAnnotated(run, "lib test");
        Assert.Contains("--tdd: generated 5 member(s) this run:", run.StdErr);
    }

    /// <summary>
    /// The test bundle listed first, on ONE cache: a plain run compiles the library clean and caches it with no
    /// edges, so a --tdd run on the same cache must compile it again to leave them (a source dependency with none
    /// is never served from the cache under --tdd); a second --tdd run does the same, and a plain run after them
    /// still refuses the library's missing members instead of being served a stub.
    /// </summary>
    [SkippableFact]
    public void PlainThenTddThenTddThenPlainOnOneCache_AnnotatesEveryTddRunAndNeverServesAStubToAPlainOne()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-testpage-cache");
        Directory.CreateDirectory(cache);
        try
        {
            using var plain = new TddRunResult(Reversed) { CacheRoot = cache, Plain = true };
            Assert.NotEqual(0, plain.Exit);
            using var first = new TddRunResult(Reversed) { CacheRoot = cache };
            AssertAnnotated(first, "tdd after a plain run");
            using var second = new TddRunResult(Reversed) { CacheRoot = cache };
            AssertAnnotated(second, "tdd after a tdd run");
            using var after = new TddRunResult(Reversed) { CacheRoot = cache, Plain = true };
            Assert.NotEqual(0, after.Exit);
            Assert.DoesNotContain(after.Tests, t => t.GetProperty("status").GetString() == "pass");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
