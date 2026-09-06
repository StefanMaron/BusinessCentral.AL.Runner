// RunnerOutOfScopeMessagePointerTests — the docs/scope.md pointer appears exactly once.
//
// Root cause being pinned (#2931)
// -------------------------------
// RunnerOutOfScopeException.BuildMessage ALWAYS appends " — see docs/scope.md[#anchor]".
// 47 throw sites across 13 files also wrote "See docs/scope.md" at the end of their own
// reason text, so those messages rendered:
//
//     out-of-scope: <api> — <reason>. See docs/scope.md — see docs/scope.md
//
// The fix normalises in the exception rather than editing 47 strings, so a throw site added
// later cannot reintroduce it. These tests pin the normalisation itself and, just as
// importantly, its LIMITS: a reason that names a specific anchor, or that mentions the file
// mid-sentence, is carrying information the appended link does not repeat and must survive.
//
// They also pin that `Reason` and the message agree. `ExpectationManifest.ReasonAnchor` reads
// the text before the first em-dash separator, so a manifest match is unaffected either way —
// but a reader comparing a manifest entry against a failure message should not see two
// different strings.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class RunnerOutOfScopeMessagePointerTests
{
    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // Each case states the text that must SURVIVE, rather than deriving it from the input with
    // string surgery. The derived form could not express the semicolon and colon cases below —
    // it cut on ". See" / ". see" literally — and a case whose expectation is computed from its
    // own input cannot say which characters the normaliser is allowed to eat.
    [Theory]
    // The exact shape RunnerPageInstance and 12 other files used.
    [InlineData("testpage-action — the page declares no OnAction trigger. See docs/scope.md",
                "testpage-action — the page declares no OnAction trigger")]
    // Same, with the trailing full stop some sites wrote.
    [InlineData("testpage-action — the page declares no OnAction trigger. See docs/scope.md.",
                "testpage-action — the page declares no OnAction trigger")]
    // Lower-case lead-in, and trailing whitespace.
    [InlineData("email-smtp — sending mail needs a real SMTP server. see docs/scope.md   ",
                "email-smtp — sending mail needs a real SMTP server")]
    // SEMICOLON LEAD-IN. Not a stylistic variant added for thoroughness — it is the form ten
    // live throw sites actually use (RecordPatches.ObjectSystemTable.cs ×6,
    // RecordPatches.UserSystemTable.cs, RecordPatches.cs, plus the twelve Object Metadata
    // refusals #2894 rewrote away from it). TrimTrailingDocPointer strips ';' via its
    // hand-written TrimEnd('.', ',', ';', ':') set, and nothing pinned that: only the full-stop
    // form above was covered, so the semicolon form's contract was asserted incidentally over in
    // ObjectMetadataProviderRowProbeTests — a test whose subject is the populate-once memo — and
    // that is the test which went red instead of this one.
    [InlineData("object-system-table — data access has no in-memory provider; see docs/scope.md",
                "object-system-table — data access has no in-memory provider")]
    // Colon lead-in — same TrimEnd set, also in live use.
    [InlineData("testpage-action — the page has no such action: see docs/scope.md",
                "testpage-action — the page has no such action")]
    public void ReasonEndingInAScopePointerYieldsExactlyOnePointerInTheMessage(
        string reason, string expectedReason)
    {
        var ex = new RunnerOutOfScopeException("SomeApi.Method", reason);

        // The reason's own content survives: only the pointer and its lead-in punctuation are
        // dropped, never the sentence in front of them.
        Assert.Equal(expectedReason, ex.Reason);
        Assert.Equal(1, CountOccurrences(ex.Message, "docs/scope.md"));
        Assert.EndsWith("— see docs/scope.md", ex.Message);
        Assert.Equal("out-of-scope: SomeApi.Method — " + expectedReason + " — see docs/scope.md",
            ex.Message);
    }

    [Fact]
    public void ReasonWithNoPointerIsLeftExactlyAsWritten()
    {
        const string Reason = "not-yet-implemented — the action declares RunObject = Report 'X' (5)";
        var ex = new RunnerOutOfScopeException("TestPage action 1 on page 2", Reason);

        Assert.Equal(Reason, ex.Reason);
        Assert.Equal("out-of-scope: TestPage action 1 on page 2 — " + Reason + " — see docs/scope.md",
            ex.Message);
    }

    [Fact]
    public void ReasonAndMessageCarryTheSameNormalisedText()
    {
        var ex = new RunnerOutOfScopeException(
            "SomeApi.Method", "testpage-action — nothing to run. See docs/scope.md");

        Assert.Equal("testpage-action — nothing to run", ex.Reason);
        Assert.Contains(ex.Reason, ex.Message);
    }

    [Fact]
    public void AnAnchoredPointerInTheReasonIsNotStripped()
    {
        // "docs/scope.md#email" is not the bare file name, so it is not the redundant trailer
        // BuildMessage appends — dropping it would lose the anchor the author chose.
        const string Reason = "email-smtp — see docs/scope.md#email";
        var ex = new RunnerOutOfScopeException("NavEmail.Send", Reason);

        Assert.Equal(Reason, ex.Reason);
        Assert.Contains("docs/scope.md#email", ex.Message);
    }

    [Fact]
    public void APointerInTheMiddleOfTheReasonIsNotStripped()
    {
        const string Reason = "email-smtp — docs/scope.md lists this surface, and it is permanent";
        var ex = new RunnerOutOfScopeException("NavEmail.Send", Reason);

        Assert.Equal(Reason, ex.Reason);
        Assert.Equal(2, CountOccurrences(ex.Message, "docs/scope.md"));
    }

    [Fact]
    public void TheDocAnchorArgumentStillProducesTheAnchoredLink()
    {
        var ex = new RunnerOutOfScopeException(
            "NavEmail.Send", "email-smtp — sending mail needs a real SMTP server", "email");

        Assert.EndsWith("— see docs/scope.md#email", ex.Message);
        Assert.Equal(1, CountOccurrences(ex.Message, "docs/scope.md"));
    }

    [Fact]
    public void TheOutOfScopeParserRecoversTheNormalisedReason()
    {
        var ex = new RunnerOutOfScopeException(
            "TestPage action 1 on page 2",
            "not-yet-implemented — nothing to run. See docs/scope.md");

        Assert.True(OutOfScopeMessage.TryParse(ex.Message, out var parsed));
        Assert.Equal("TestPage action 1 on page 2", parsed.Api);
        // Parsed-from-message and the typed property agree — the whole point of normalising in
        // the constructor rather than only in BuildMessage.
        Assert.Equal(ex.Reason, parsed.Reason);
    }
}
