// --tdd and the compiled-deps cache, for the edges #5286 adds (TddLibTriggerEvents): the call edges of a
// source dependency are in-process state, never in the cache, so an entry written by a run that recorded none
// (a plain run, or a build of the runner older than the edges) must not be served to a --tdd run that needs them.
//
// Runner-specific, so it lives here, not in the al-language corpus.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddLibTriggerEventsCacheTests
{
    /// <summary>
    /// A plain run compiles the app clean and caches it with no edges, then a --tdd run on the same cache
    /// must compile it again, or the tests that call its procedures lose their annotation; a second --tdd run
    /// does the same, and a plain run after them still refuses the library's missing members instead of being
    /// served a stub.
    /// </summary>
    [SkippableFact]
    public void PlainThenTddThenTddThenPlainOnOneCache_AnnotatesEveryTddRunAndNeverServesAStubToAPlainOne()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-trigger-cache");
        Directory.CreateDirectory(cache);
        try
        {
            using var plain = new TddRunResult(TddLibTriggerEventsRun.ReversedFolders) { CacheRoot = cache, Plain = true };
            Assert.NotEqual(0, plain.Exit);
            using var first = new TddRunResult(TddLibTriggerEventsRun.ReversedFolders) { CacheRoot = cache };
            TddLibTriggerEventsTests.AssertAnnotated(first, "tdd after a plain run");
            using var second = new TddRunResult(TddLibTriggerEventsRun.ReversedFolders) { CacheRoot = cache };
            TddLibTriggerEventsTests.AssertAnnotated(second, "tdd after a tdd run");
            using var after = new TddRunResult(TddLibTriggerEventsRun.ReversedFolders) { CacheRoot = cache, Plain = true };
            Assert.NotEqual(0, after.Exit);
            Assert.DoesNotContain(after.Tests, t => t.GetProperty("status").GetString() == "pass");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
