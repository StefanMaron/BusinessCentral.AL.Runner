// --tdd where the missing member is on a codeunit of the SAME test library that calls it (#5271):
// `al-runner --tdd lib test`, the library's "Lib Own Same" calling "SameMissing" on its own
// "Lib Own Other". The member is generated into the library's own compile, so no other bundle's
// recompile is involved and no [Test] sits in that compile: the tests that call into the library
// are in the test bundle, which never sees the call. Without the annotation they pass against the stub
// silently, and with the test bundle listed first the closing line said no test referenced a missing
// symbol.
//
// Runner-specific (--tdd and the runner's own cache), so it lives here, not in the al-language corpus.
using Xunit;

namespace AlRunner.Tests;

/// <summary>The library and the test bundle in dependency order, one run on a fresh cache.</summary>
public sealed class TddLibOwnMemberRun : TddRunResult
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibOwnMember");

    public static string[] ReversedFolders { get; } = { Path.Combine(Root, "test"), Path.Combine(Root, "lib") };

    public TddLibOwnMemberRun() : base(Path.Combine(Root, "lib"), Path.Combine(Root, "test")) { }
}

public sealed class TddLibOwnMemberTests : IClassFixture<TddLibOwnMemberRun>
{
    private readonly TddLibOwnMemberRun _run;

    public TddLibOwnMemberTests(TddLibOwnMemberRun run) => _run = run;

    private const string Stub = "Lib Own Other: procedure \"SameMissing\"(Arg1: Integer): Integer";

    private static void AssertGeneratedAndAnnotated(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 0, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(2, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));
        Assert.Contains("--tdd: generated 1 member(s) this run:", run.StdErr);
        Assert.DoesNotContain("no test referenced a missing symbol", run.StdErr);
        Assert.Equal(new[] { Stub }, run.StubsOf("SameModule_Stub"));
        // The library's other object touches nothing missing.
        Assert.Empty(run.StubsOf("OtherLibraryObject_IsNotAnnotated"));
    }

    /// <summary>
    /// The member is generated into the library's own compile and the test bundle's test is named as
    /// reaching it, which only the library's compile can say.
    /// </summary>
    [SkippableFact]
    public void MemberOnTheLibrarysOwnCodeunit_IsGeneratedAndTheTestThatCallsTheLibraryIsAnnotated()
    {
        TestArtifacts.SkipIfMissing();
        AssertGeneratedAndAnnotated(_run, "lib test");
        Assert.Contains("--tdd: 1 test(s) reach generated stubs this run:", _run.StdErr);
    }

    /// <summary>
    /// The test bundle first: the library is compiled by its dependency load and reports nothing through
    /// a bundle iteration of its own, and on the second run against ONE cache root it must still be
    /// compiled (a compiled-deps hit would generate nothing). Cold and warm answer alike, and a plain run in
    /// between is not served the stub.
    /// </summary>
    [SkippableFact]
    public void TestBundleListedFirst_ListsAndAnnotatesOnAColdAndOnAWarmCache()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-own-member-warm");
        Directory.CreateDirectory(cache);
        try
        {
            using var cold = new TddRunResult(TddLibOwnMemberRun.ReversedFolders) { CacheRoot = cache };
            AssertGeneratedAndAnnotated(cold, "cold");
            // The other direction, and the more dangerous one: a plain run after a --tdd run on one cache must
            // not be served the library compiled with the generated stub. The member is missing for it, so the
            // test that calls it fails; served the stub it would pass, exit 0, with nothing said.
            using var plain = new TddRunResult(TddLibOwnMemberRun.ReversedFolders) { CacheRoot = cache, Plain = true };
            Assert.True(plain.Exit != 0, $"plain after --tdd: exit {plain.Exit}\n{plain.StdErr}");
            Assert.Equal("fail", plain.Find("SameModule_Stub").GetProperty("status").GetString());
            using var warm = new TddRunResult(TddLibOwnMemberRun.ReversedFolders) { CacheRoot = cache };
            AssertGeneratedAndAnnotated(warm, "warm");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
