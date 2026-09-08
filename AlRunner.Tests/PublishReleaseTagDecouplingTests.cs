// PublishReleaseOrderingTests — the release ships a COMMIT, not a branch state (#3494).
//
// WHAT WENT WRONG, AND WHY A TEST RATHER THAN A COMMENT
//   publish.yml used to push the CHANGELOG commit to the branch FIRST and create the tag
//   only if that push was a fast-forward. v2.11.0 ran all eight BC legs green, built,
//   signed and packed, and then threw the lot away: PRs #3484 and #3469 merged during the
//   run, the push was rejected as non-fast-forward, and no tag, no NuGet push and no
//   GitHub Release followed. About fifty minutes of an account-wide Actions queue for
//   nothing.
//
//   The old ordering was defended in a code comment as "the whole point of #1978". It is
//   not. #1978's property is NEVER TAG A COMMIT WHOSE TESTS DID NOT PASS, and that is
//   satisfied by the matrix, the build and the pack all running before the tag step.
//   "The branch has not moved" is a separate property that says nothing about whether the
//   commit being shipped is good — commits merged after the tested one are by definition
//   not in the release.
//
//   A comment saying so is what was there before, pointing the other way. These assertions
//   are what keep the ordering from being reverted by someone reading #1978 the same way.
using System;
using System.IO;
using Xunit;

namespace AlRunner.Tests;

public sealed class PublishReleaseTagDecouplingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string ReleaseStep() => WorkflowStep.BodyOf(
        File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "publish.yml")),
        "Generate CHANGELOG and push the release commit + tag");

    /// <summary>
    /// The tag push must come BEFORE the branch push. This is the assertion that fails on
    /// the pre-#3494 file, where the order was the other way round and the branch push
    /// gated the tag.
    /// </summary>
    [Fact]
    public void TheTagIsPushed_BeforeTheChangelogCommit()
    {
        var body = ReleaseStep();

        var tagPush = body.IndexOf("git push origin \"$TAG\"", StringComparison.Ordinal);
        var branchPush = body.IndexOf("git push origin HEAD:", StringComparison.Ordinal);

        Assert.True(tagPush >= 0, "the release step no longer pushes a tag at all");
        Assert.True(branchPush >= 0, "the release step no longer pushes the CHANGELOG commit");
        Assert.True(tagPush < branchPush,
            "the tag must be pushed BEFORE the CHANGELOG commit. With the branch push first, a "
            + "branch that moved during the run rejects it as non-fast-forward and NO TAG IS EVER "
            + "CREATED — a fully tested, built and signed release is discarded because of commits "
            + "that are not even in it (#3494). A tag push needs no fast-forward.");
    }

    /// <summary>
    /// The tag must name the TESTED commit explicitly. A bare `git tag "$TAG"` tags whatever
    /// HEAD is, which after the CHANGELOG commit is a commit that the regenerate-and-retry
    /// path can leave off the branch's history — and then the NEXT release's
    /// `git describe --tags --abbrev=0` does not find it and computes its range from the
    /// wrong baseline. That breaks one release later, which is the worst time to find out.
    /// </summary>
    [Fact]
    public void TheTag_NamesTheTestedCommit_NotWhateverHeadIs()
    {
        var body = ReleaseStep();

        Assert.Contains("TESTED_SHA=$(git rev-parse HEAD)", body);
        Assert.Contains("git tag \"$TAG\" \"$TESTED_SHA\"", body);
        Assert.False(body.Contains("git tag \"$TAG\"\n", StringComparison.Ordinal),
            "`git tag \"$TAG\"` with no commit argument tags HEAD, which by then is the CHANGELOG "
            + "commit rather than the commit that was tested and built.");
    }

    /// <summary>
    /// Failing to land the CHANGELOG commit must not fail a release that is already tagged
    /// and about to publish. The push therefore has to sit inside a retry whose failure is
    /// reported, not propagated.
    /// </summary>
    [Fact]
    public void FailingToLandTheChangelog_DoesNotFailTheRelease()
    {
        var body = ReleaseStep();

        Assert.Contains("CHANGELOG_PUSHED=false", body);
        Assert.Contains("if git push origin HEAD:", body);
        Assert.Contains("::warning::", body);

        // The retry regenerates rather than rebases. $COMMITS is fixed to the tested range,
        // so replaying it reproduces the same section; a rebase would drag commits that
        // merged after the release under this version's heading.
        Assert.Contains("git reset --hard \"origin/", body);
        Assert.Contains("generate_changelog.py", body);
        Assert.False(body.Contains("git rebase", StringComparison.Ordinal),
            "the retry must regenerate the section onto the branch's new head, not rebase — a "
            + "rebase would put commits that merged AFTER the tested commit under this release's "
            + "heading.");
    }

    /// <summary>
    /// The tag no longer carries the CHANGELOG commit, so the tag-exists reuse path cannot
    /// read the section out of the tag alone. Left unchanged it would return nothing and the
    /// release would publish with empty notes — a silent wrong answer rather than a failure.
    /// </summary>
    [Fact]
    public void TheReusePath_ReadsTheSectionFromTheBranch_AndRefusesWhenItFindsNone()
    {
        var body = ReleaseStep();

        Assert.Contains("read_section \"origin/", body);
        Assert.Contains("::error::", body);
        Assert.Contains("Refusing to publish a release with empty notes", body);
    }
}
