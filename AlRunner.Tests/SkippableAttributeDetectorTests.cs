// SkippableAttributeDetectorTests -- the detector behind
// TestArtifactsGateTests.EveryTestThatCanSkipIsDeclaredSkippable, exercised against synthetic
// sources instead of the suite's own contents (issue #3813).
//
// That guard was a textual scanner that matched the skip-call spelling ANYWHERE in a line,
// comments included, and attributed the match to whichever test declaration preceded it. So a
// test's required attribute was decided by prose and line position rather than by whether the
// test can skip: an edit changing no behaviour changed whether CI passed, and the failure named
// a test that had nothing to do with the match.
//
// It fired four times, twice inside comments that were explaining the defect. This file holds
// the two directions apart -- a real call in a plain [Fact] is still reported, and the same
// spelling in a comment is not -- against inputs this file controls, so a future narrowing of
// the detector fails here rather than going unnoticed until nobody writes the shape it misses.
//
// This file is exempt from the guard for the same reason SilentSkipDetectorTests.cs is exempt
// from the silent-skip guard: its synthetic offenders are literals in it.
using Xunit;

namespace AlRunner.Tests;

public sealed class SkippableAttributeDetectorTests
{
    private static IReadOnlyList<string> Offenders(string source) =>
        TestArtifactsGateTests.FindTestsThatCanSkipButAreNotSkippable("Probe.cs", source).ToList();

    // ---- the defect: a comment must not decide anything -------------------------

    /// <summary>
    /// #3813's exact shape, and the reason this file exists. The method body's prose mentions
    /// the skip helper while the method cannot reach one -- reproduced verbatim from
    /// MetadataEquivalenceBundleGateTests, where an unnecessary [SkippableFact] was held in
    /// place by a comment until this fix removed the need for it.
    /// </summary>
    [Fact]
    public void AProseCommentNamingTheHelperIsNotACall()
    {
        var source = """
            [Fact]
            public void CannotSkip()
            {
                // a class could hold a bundles list and still add a defensive
                // TestArtifacts.SkipIf on its count -- which would be dead code.
                Assert.Equal(0, offenders.Length);
            }
            """;

        Assert.Empty(Offenders(source));
    }

    /// <summary>
    /// The other spelling the guard matches, in the doc-comment form that tripped it on
    /// BcEngineUnbootstrappedGuardTests (PR #3843): two <c>///</c> lines describing what the
    /// guard deliberately does NOT do, attributed to two tests that are pure functions.
    /// </summary>
    [Fact]
    public void ADocCommentNamingTheHelperIsNotACall()
    {
        var source = """
            /// <summary>
            /// Deliberately does not call TestArtifacts.SkipIf: an unbootstrapped box is a
            /// defect here, not an absent prerequisite, so Skip.If would hide it.
            /// </summary>
            [Theory]
            [InlineData(1)]
            public void CannotSkip(int n)
            {
                Assert.Equal(n, n);
            }
            """;

        Assert.Empty(Offenders(source));
    }

    /// <summary>A trailing comment on a line of real code: only the comment names the helper.</summary>
    [Fact]
    public void ATrailingCommentNamingTheHelperIsNotACall()
    {
        var source = """
            [Fact]
            public void CannotSkip()
            {
                Assert.True(ready); // not Skip.If(!ready, ...): absence here is a defect
            }
            """;

        Assert.Empty(Offenders(source));
    }

    /// <summary>Block comments, including one that opens and closes mid-line.</summary>
    [Theory]
    [InlineData("    /* TestArtifacts.SkipIf would be wrong here */")]
    [InlineData("    Assert.True(ready); /* not Skip.Always */")]
    public void ABlockCommentNamingTheHelperIsNotACall(string commentLine)
    {
        var source = "[Fact]\npublic void CannotSkip()\n{\n" + commentLine + "\n    Assert.True(ready);\n}";

        Assert.Empty(Offenders(source));
    }

    /// <summary>
    /// A block comment spanning several lines, which is the form a line-at-a-time stripper
    /// gets wrong: only the opening line carries <c>/*</c>, so the lines after it look like
    /// ordinary code to anything that does not carry state across the newline.
    /// </summary>
    [Fact]
    public void AMultiLineBlockCommentNamingTheHelperIsNotACall()
    {
        var source = """
            [Fact]
            public void CannotSkip()
            {
                /*
                   TestArtifacts.SkipIf is deliberately absent here.
                   Skip.Always would be worse.
                */
                Assert.True(ready);
            }
            """;

        Assert.Empty(Offenders(source));
    }

    // ---- the guard's real job, which the fix must not weaken --------------------

    /// <summary>
    /// The true positive, and the half easiest to lose while making a scanner ignore comments:
    /// a real call in a plain <c>[Fact]</c> body still has to be reported, and named correctly.
    /// A SkipException out of a plain [Fact] is recorded Failed, not Skipped.
    /// </summary>
    [Fact]
    public void ARealCallInAPlainFactIsReported()
    {
        var source = """
            [Fact]
            public void CanSkip()
            {
                TestArtifacts.SkipIf(!ready, "no engine");
                Assert.True(ready);
            }
            """;

        var offenders = Offenders(source);

        Assert.Single(offenders);
        Assert.Contains("Probe.cs.CanSkip", offenders[0], StringComparison.Ordinal);
        Assert.Contains("[Fact]", offenders[0], StringComparison.Ordinal);
    }

    /// <summary>Every spelling the detector claims to know, each one alone in a plain [Fact].</summary>
    [Theory]
    [InlineData("TestArtifacts.SkipIf(cond, \"r\");")]
    [InlineData("TestArtifacts.SkipIfMissing();")]
    [InlineData("TestArtifacts.SkipIfDirectoryMissing(dir, \"what\");")]
    [InlineData("Skip.If(cond, \"r\");")]
    [InlineData("Skip.IfNot(cond, \"r\");")]
    [InlineData("Skip.Always(\"r\");")]
    public void EverySkipSpellingIsStillDetected(string call)
    {
        var source = "[Fact]\npublic void CanSkip()\n{\n    " + call + "\n}";

        Assert.Single(Offenders(source));
    }

    /// <summary>
    /// A real call on a line that ALSO carries a comment. The comment-aware pass must strip the
    /// comment and keep the code, not discard the whole line -- discarding it is the cheap
    /// mistake that would turn this guard silent while looking like a fix.
    /// </summary>
    [Fact]
    public void ARealCallIsStillDetectedWhenTheLineAlsoHasAComment()
    {
        var source = """
            [Fact]
            public void CanSkip()
            {
                Skip.If(!ready, "no engine"); // absent artifacts are not this test's business
            }
            """;

        Assert.Single(Offenders(source));
    }

    /// <summary>A [Theory] is reported exactly as a [Fact] is, and named as one.</summary>
    [Fact]
    public void ARealCallInAPlainTheoryIsReported()
    {
        var source = """
            [Theory]
            [InlineData(1)]
            public void CanSkip(int n)
            {
                Skip.If(n == 0, "zero");
            }
            """;

        var offenders = Offenders(source);

        Assert.Single(offenders);
        Assert.Contains("[Theory]", offenders[0], StringComparison.Ordinal);
    }

    /// <summary>The correctly-declared spellings are what the guard exists to push people to.</summary>
    [Theory]
    [InlineData("SkippableFact")]
    [InlineData("SkippableTheory")]
    public void ADeclaredSkippableTestIsNotReported(string attribute)
    {
        var source = "[" + attribute + "]\npublic void CanSkip()\n{\n    Skip.If(!ready, \"no engine\");\n}";

        Assert.Empty(Offenders(source));
    }

    // ---- strings are code, not comments -----------------------------------------

    /// <summary>
    /// A <c>//</c> inside a string literal opens no comment, so the code after it on the same
    /// line is still code. 116 lines in this suite carry that shape -- URLs in XML manifests,
    /// and AlSourceParserCommentTests' own AL fixtures -- and a stripper that cut at the first
    /// <c>//</c> would silently truncate every one of them.
    /// </summary>
    [Fact]
    public void ASlashSlashInsideAStringDoesNotHideARealCallAfterIt()
    {
        var source = """
            [Fact]
            public void CanSkip()
            {
                Assert.Equal("http://example/x", url); Skip.If(!ready, "no engine");
            }
            """;

        Assert.Single(Offenders(source));
    }

    /// <summary>The same, for a verbatim string, where a backslash is not an escape.</summary>
    [Fact]
    public void ASlashSlashInsideAVerbatimStringDoesNotHideARealCallAfterIt()
    {
        var source = "[Fact]\npublic void CanSkip()\n{\n"
            + "    var p = @\"C:\\a//b\"; Skip.If(!ready, \"no engine\");\n}";

        Assert.Single(Offenders(source));
    }

    /// <summary>
    /// And a raw string literal, where neither backslash escapes nor doubled quotes apply and
    /// the terminator is the fence itself.
    /// </summary>
    [Fact]
    public void ASlashSlashInsideARawStringDoesNotHideARealCallAfterIt()
    {
        var source = "[Fact]\npublic void CanSkip()\n{\n"
            + "    var s = \"\"\"a // b\"\"\"; Skip.If(!ready, \"no engine\");\n}";

        Assert.Single(Offenders(source));
    }

    /// <summary>
    /// The converse, and the one that decides whether the string handling earns its keep: a
    /// skip spelling that only ever appears INSIDE a string literal is not a call either. This
    /// is the shape MetadataEquivalenceBundleGateTests' own regex has -- and the one the
    /// issue's first account misattributed the failure to.
    /// </summary>
    [Fact]
    public void ASkipSpellingInsideAStringIsNotACall()
    {
        var source = """
            [Fact]
            public void CannotSkip()
            {
                var pattern = new Regex("Skip.If" + suffix);
                Assert.NotNull(pattern);
            }
            """;

        Assert.Empty(Offenders(source));
    }

    /// <summary>A char literal holding a quote must not be read as opening a string.</summary>
    [Fact]
    public void ACharLiteralQuoteDoesNotSwallowTheRestOfTheFile()
    {
        var source = """
            [Fact]
            public void CanSkip()
            {
                var q = '"';
                Skip.If(!ready, "no engine");
            }
            """;

        Assert.Single(Offenders(source));
    }

    // ---- attribution, the second half of the reported defect ---------------------

    /// <summary>
    /// The guard reported the wrong test name, because a match was attributed to whichever
    /// declaration preceded the line. With comments excluded there is no stray match to
    /// attribute -- so the test named here is the one that actually calls.
    /// </summary>
    [Fact]
    public void TheReportedNameIsTheTestThatActuallyCalls()
    {
        var source = """
            [Fact]
            public void FirstCannotSkip()
            {
                Assert.True(true);
            }

            // TestArtifacts.SkipIf belongs to neither of these.

            [Fact]
            public void SecondCanSkip()
            {
                Skip.Always("always");
            }
            """;

        var offenders = Offenders(source);

        Assert.Single(offenders);
        Assert.Contains("SecondCanSkip", offenders[0], StringComparison.Ordinal);
        Assert.DoesNotContain("FirstCannotSkip", offenders[0], StringComparison.Ordinal);
    }

    /// <summary>Several offending tests in one file are all reported, not just the first.</summary>
    [Fact]
    public void EveryOffenderInAFileIsReported()
    {
        var source = """
            [Fact]
            public void OneCanSkip()
            {
                Skip.Always("a");
            }

            [Fact]
            public void TwoCanSkip()
            {
                Skip.Always("b");
            }
            """;

        Assert.Equal(2, Offenders(source).Count);
    }

    // ---- the comment stripper, on its own ----------------------------------------

    /// <summary>
    /// The stripper replaces comment text rather than deleting the line, so a line number in
    /// the stripped text still means the same line of the original. Without that, attribution
    /// -- the half of #3813 that produced wrong test names -- would break in a new way.
    /// </summary>
    [Fact]
    public void StrippingPreservesTheLineCount()
    {
        var source = """
            var a = 1; // one
            /* two
               three */
            var b = 2;
            """;

        Assert.Equal(source.Split('\n').Length,
                     TestArtifactsGateTests.StripCommentsPreservingLines(source).Split('\n').Length);
    }

    /// <summary>Code on the same line as a comment survives; the comment does not.</summary>
    [Fact]
    public void StrippingKeepsTheCodeAndDropsTheComment()
    {
        var stripped = TestArtifactsGateTests.StripCommentsPreservingLines("var a = 1; // set a to one");

        Assert.Contains("var a = 1;", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("set a to one", stripped, StringComparison.Ordinal);
    }

    /// <summary>An unterminated block comment swallows the rest of the file, as the compiler
    /// would have it -- and must not throw, because a guard that throws on a file it cannot
    /// parse reports nothing about every other file in the suite.</summary>
    [Fact]
    public void AnUnterminatedBlockCommentSwallowsTheRestWithoutThrowing()
    {
        var stripped = TestArtifactsGateTests.StripCommentsPreservingLines("var a = 1;\n/* open\nSkip.Always(\"x\");\n");

        Assert.Contains("var a = 1;", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("Skip.Always", stripped, StringComparison.Ordinal);
    }

    // ---- the sibling guard in the same file, which had the same blind spot -------

    /// <summary>
    /// <c>OnlyTheSharedHelperNamesTheArtifactCachePathsInCode</c> documents that "comments may
    /// still discuss the paths", and skipped a line whose FIRST characters were <c>//</c> to
    /// achieve it. That caught a comment on its own line and missed a trailing one and a block
    /// one, so the same prose-decides-the-verdict defect lived in the function next door. It
    /// now shares this file's stripper, which is what makes the two agree.
    /// </summary>
    [Theory]
    [InlineData("    // the .bcartifacts.cache layout is legacy")]
    [InlineData("    var x = 1; // the .bcartifacts.cache layout is legacy")]
    [InlineData("    /* .bcartifacts.cache is the legacy layout */")]
    [InlineData("    /// <summary>.bcartifacts.cache is legacy</summary>")]
    public void ACommentNamingAnArtifactPathIsNotCode(string line)
        => Assert.Empty(TestArtifactsGateTests.FindHardCodedArtifactPaths("Probe.cs", line));

    /// <summary>The true positive it exists for: a path spelled in real code is still reported.</summary>
    [Theory]
    [InlineData("    var dir = Path.Combine(home, \".bcartifacts.cache\", \"sandbox\");")]
    [InlineData("    var dir = Path.Combine(home, \"al-runner\", \"artifacts\");")]
    public void APathSpelledInCodeIsStillReported(string line)
        => Assert.Single(TestArtifactsGateTests.FindHardCodedArtifactPaths("Probe.cs", line));

    // ---- the guard must not report clean when it measured nothing ----------------

    /// <summary>
    /// guards-need-a-third-state.md: a source carrying no test declaration at all yields no
    /// offenders, which is a legitimate pass for a helper file -- the non-vacuity claim lives
    /// on the suite-wide scan, which asserts it found test declarations to look at.
    /// </summary>
    [Fact]
    public void AFileWithNoTestDeclarationsYieldsNoOffenders()
        => Assert.Empty(Offenders("internal static class Helper { internal static void X() { } }"));

    /// <summary>
    /// The non-vacuity backstop itself: the suite-wide scan must have seen test declarations.
    /// Zero offenders out of zero declarations examined is how this guard passes having
    /// measured nothing, which is the same shape as the defect it guards against.
    /// </summary>
    [Fact]
    public void TheSuiteWideScanExaminesTestDeclarations()
    {
        Assert.True(
            TestArtifactsGateTests.CountTestDeclarations() > TestArtifactsGateTests.MinimumTestDeclarations,
            "the suite-wide skippable-attribute scan found almost no [Fact]/[Theory] declarations, "
            + "so its 'no offenders' verdict is about nothing.");
    }
}
