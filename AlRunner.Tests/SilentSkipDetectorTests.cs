// SilentSkipDetectorTests — the drift guard that forbids a test returning early because its
// environment is unavailable (issue #2620).
//
// A test that returns instead of skipping is recorded Passed: a green tick meaning "asserted
// nothing". TestArtifactsGateTests.NoTestSilentlyReturnsWhenItsEnvironmentIsUnavailable exists to
// forbid that, and it could only ever be exercised by the suite's own contents — so a hole in it
// stayed invisible for as long as nobody happened to write the shape it missed.
//
// It missed one. The first pattern was `\[skip\][^\n]*\breturn;` with RegexOptions.None, and
// `[^\n]*` cannot cross a newline, so it only matched the one-line form and was blind to the same
// defect written over two lines — which is how anyone would actually write it.
using Xunit;

namespace AlRunner.Tests;

public sealed class SilentSkipDetectorTests
{
    private static int Offences(string source) =>
        TestArtifactsGateTests.FindSilentSkipReturns(source).Count();

    // ---- the shape the old pattern could not see --------------------------------

    /// <summary>
    /// The multi-line form: a "[skip]" report on one line, the return on the next. Red against
    /// `[^\n]*`, which cannot cross the newline between them.
    /// </summary>
    [Fact]
    public void DetectsASkipReportAndReturnOnSeparateLines()
    {
        var source = """
            [Fact]
            public void SomeTest()
            {
                if (!engine.Ready)
                {
                    Console.Error.WriteLine($"[skip] BC engine not available");
                    return;
                }
                Assert.Equal(1, 1);
            }
            """;

        Assert.Equal(1, Offences(source));
    }

    /// <summary>The one-line form the old pattern did catch, so widening it did not lose anything.</summary>
    [Fact]
    public void StillDetectsASkipReportAndReturnOnOneLine()
    {
        Assert.Equal(1, Offences("if (!ready) { Console.Error.WriteLine(\"[skip] no engine\"); return; }"));
    }

    /// <summary>The second shape, unchanged: a bare return whose comment admits what it is doing.</summary>
    [Theory]
    [InlineData("return; // skip: no artifacts here")]
    [InlineData("return; // not provisioned")]
    [InlineData("return; // nothing to assert")]
    [InlineData("return; // nothing to prove without the engine")]
    public void DetectsABareReturnWhoseCommentAdmitsIt(string line)
    {
        Assert.Equal(1, Offences(line));
    }

    // ---- and does not fire on things that are fine ------------------------------

    /// <summary>The correct spelling must not be reported. This is the whole point of the guard:
    /// it exists to push people to SkipIf, so flagging SkipIf would be self-defeating.</summary>
    [Fact]
    public void DoesNotFlagAVisibleSkip()
    {
        var source = """
            [SkippableFact]
            public void SomeTest()
            {
                TestArtifacts.SkipIf(!engine.Ready, "BC engine not available");
                Assert.Equal(1, 1);
            }
            """;

        Assert.Equal(0, Offences(source));
    }

    /// <summary>An ordinary early return in a helper is not a silent skip, and must not be
    /// reported — a guard that fires on ordinary code gets suppressed and then protects nothing.</summary>
    [Fact]
    public void DoesNotFlagAnOrdinaryEarlyReturn()
    {
        var source = """
            private static void Helper(string? value)
            {
                if (value == null) return;
                Console.WriteLine(value);
            }
            """;

        Assert.Equal(0, Offences(source));
    }

    /// <summary>
    /// The bound is what keeps Singleline from over-matching: a "[skip]" mentioned in one method
    /// and an unrelated `return;` far below it are two different pieces of code, and reporting
    /// them as one offence would send the reader to the wrong place.
    /// </summary>
    [Fact]
    public void DoesNotJoinASkipMentionToAReturnFarBelowIt()
    {
        var source = "Console.WriteLine(\"[skip] historical note\");\n"
            + string.Join("\n", Enumerable.Repeat("    DoSomething();", 60))
            + "\n    return;";

        Assert.Equal(0, Offences(source));
    }

    [Fact]
    public void ReportsNothingForSourceWithNeitherShape()
        => Assert.Equal(0, Offences("public void T() { Assert.True(true); }"));

    /// <summary>Every offence is reported, not just the first — a class that drifted usually
    /// drifted in several places at once, and a guard naming one of them costs a round trip
    /// for each of the others.</summary>
    [Fact]
    public void ReportsEveryOffenceInAFile()
    {
        var source = """
            void A()
            {
                Console.Error.WriteLine("[skip] one");
                return;
            }
            void B()
            {
                Console.Error.WriteLine("[skip] two");
                return;
            }
            """;

        Assert.Equal(2, Offences(source));
    }
}
