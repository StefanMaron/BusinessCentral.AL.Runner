// --tdd over a test library one of whose codeunits is dropped for a call --tdd refuses to generate for (#5266):
// `al-runner --tdd lib test`, the library's "Lib Dropped Refused" calling a member of "Lib Dropped Other" as the
// argument of exit(). The codeunit is not in the compiled library, and a test that reaches it used to fail with
// advice to provision a package, which brings nothing back. It now names the object and its AL error.
//
// Two listings, because two compiles can drop it: the library's own bundle iteration (lib first) and the test
// bundle's dependency load (test first). Runner-specific, so it lives here, not in the al-language corpus.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddLibDroppedRun : TddRunResult
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibDropped");

    public static string[] ReversedFolders { get; } = { Path.Combine(Root, "test"), Path.Combine(Root, "lib") };

    public TddLibDroppedRun() : base(Path.Combine(Root, "lib"), Path.Combine(Root, "test")) { }
}

public sealed class TddLibDroppedTests : IClassFixture<TddLibDroppedRun>
{
    private readonly TddLibDroppedRun _run;

    public TddLibDroppedTests(TddLibDroppedRun run) => _run = run;

    private static void AssertDroppedObjectIsNamed(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 1, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(2, run.Tests.Count);
        // The control: the library's other codeunit compiled, and its test runs.
        Assert.Equal("pass", run.Find("OtherLibraryObject_StillRuns").GetProperty("status").GetString());

        Assert.Equal("fail", run.Find("RefusedShape_FailsNamingTheDroppedObject").GetProperty("status").GetString());
        var failure = run.Failure("RefusedShape_FailsNamingTheDroppedObject");
        Assert.Contains("\"Lib Dropped Refused\"", failure);
        Assert.Contains("did not compile", failure);
        Assert.Contains("RefusedMissing", failure);
        Assert.DoesNotContain("provision", failure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--package-cache", failure);
        // The same app name from both compiles that can drop it, and a tag that exists in this run mode.
        Assert.Contains("is declared by Runner Tests Fixture - Tdd Lib Dropped Lib, a source dependency", failure);
        Assert.Contains("TDD-EXCLUDED or EMIT-EXCLUDED line", failure);

        // #5271: the closing line counts the dropped object whichever compile dropped it, and never says no
        // test referenced a missing symbol (the issue's own reproducer, listed test first, said that).
        Assert.DoesNotContain("no test referenced a missing symbol", run.StdErr);
        Assert.Contains("--tdd: no members were generated this run — 1 object(s) could not be compiled " +
            "(named by the TDD-EXCLUDED or EMIT-EXCLUDED line above)", run.StdErr);
        Assert.Empty(run.StubsOf("RefusedShape_FailsNamingTheDroppedObject"));
    }

    /// <summary>The library's own bundle iteration drops the codeunit, and the test bundle's run finds it missing.</summary>
    [SkippableFact]
    public void LibraryListedFirst_NamesTheDroppedCodeunitAndItsAlError()
    {
        TestArtifacts.SkipIfMissing();
        AssertDroppedObjectIsNamed(_run, "lib test");
    }

    /// <summary>The test bundle's dependency load drops it: no bundle iteration of the library compiled anything.</summary>
    [SkippableFact]
    public void TestBundleListedFirst_NamesTheDroppedCodeunitAndItsAlError()
    {
        TestArtifacts.SkipIfMissing();
        using var reversed = new TddRunResult(TddLibDroppedRun.ReversedFolders);
        AssertDroppedObjectIsNamed(reversed, "test lib");
    }
}
