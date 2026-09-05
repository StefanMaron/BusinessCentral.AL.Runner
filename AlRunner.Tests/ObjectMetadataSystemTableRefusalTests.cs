// ObjectMetadataSystemTableRefusalTests — what the twelve refusals in
// RecordPatches.ObjectMetadataSystemTable.cs actually claim, and where they send the reader.
//
// WHY THIS IS A RUNNER-SIDE MECHANISM TEST AND NOT AN AL BUNDLE
// ------------------------------------------------------------
// Every one of the twelve fires only when something the runner reflects on is absent or the
// wrong shape: BC's SystemTables type, its ApplicationDatabaseTables list, NavEnvironment,
// the "Object Type" option string, the in-memory data provider, TempTableDataProvider's
// private primaryTree field. None of those is reachable from AL — no AL statement can make
// SystemTables disappear — so tests/runner-extras/object-metadata-system-table cannot drive a
// single one of them, and does not try (it asserts the rows, not the refusals). The subject
// here is the C# refusal contract, so per .claude/rules/bc-behavior-tests-go-upstream.md this
// is runner-specific and belongs in AlRunner.Tests, the same shape as
// TryFunctionOutOfScopeTrapTests.
//
// Two of the twelve — the ProviderHasAnyRow pair — ARE drivable from C# with a fake provider,
// and ObjectMetadataProviderRowProbeTests does exactly that. This class covers the contract
// all twelve share; that one covers those two end to end.
//
// WHAT WAS WRONG (#2894)
// ----------------------
// All twelve pointed at docs/scope.md, which contains no object-metadata text at all, and
// they did it twice over: the reason string ended "; see docs/scope.md" and BuildMessage
// appended its own default link, so a developer read
//
//     ... has no in-memory provider; see docs/scope.md — see docs/scope.md
//
// The bigger defect is what "docs/scope.md" ASSERTS. That file is the permanently-out-of-scope
// manifest — SMTP, HTTP egress, printing. Object Metadata (2000000071) is none of those: the
// file these refusals live in IMPLEMENTS the table, and .claude/rules/loud-failures.md puts
// AL records squarely in scope. Claiming otherwise tells the next developer to stop looking,
// and it had a measurable runtime consequence: ApplicationObjectBasePatches.IsPermanentOutOfScope
// traps a refusal into `false` for an AL [TryFunction] unless its reason starts
// "not-yet-implemented". So a shape gap in this table read as a clean `if not TryX()` — the
// silent-default failure loud-failures.md exists to prevent.

using System;
using System.IO;
using System.Linq;
using Xunit;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;

namespace AlRunner.Tests;

public sealed class ObjectMetadataSystemTableRefusalTests
{
    private const string Api = "Object Metadata (system table 2000000071)";
    private const string DocLink = "docs/limitations.md#object-metadata-system-table";
    private const string SourceFile = "AlRunner/Patches/RecordPatches.ObjectMetadataSystemTable.cs";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static RunnerOutOfScopeException Refusal(string detail = "NavEnvironment.EmitVersion not found")
        => RecordPatches.ObjectMetadataShapeGap(detail);

    /// <summary>A genuinely permanent refusal, for the negative half of every pair below.</summary>
    private static RunnerOutOfScopeException PermanentRefusal()
        => new("NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email");

    // ── The link: one of them, and it points at the section that exists ──────────────────

    [Fact]
    public void Refusal_LinksToTheDocSectionThatActuallyDocumentsThisTable()
    {
        var msg = Refusal().Message;

        Assert.EndsWith(" — see " + DocLink, msg, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/scope.md", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_RendersExactlyOneDocLink()
    {
        var msg = Refusal().Message;

        // Counted on "see docs/", not on the " — see " separator: the defect was a reason
        // string that ended "; see docs/scope.md" with BuildMessage appending its own
        // " — see docs/scope.md" after it, which leaves the separator count at 1.
        var links = msg.Split("see docs/").Length - 1;
        Assert.Equal(1, links);
    }

    [Fact]
    public void TheDocAnchorTheRefusalPointsAt_Exists_AndScopeMdStillDoesNotDocumentThisTable()
    {
        var limitations = File.ReadAllText(Path.Combine(RepoRoot, "docs", "limitations.md"));
        var scope = File.ReadAllText(Path.Combine(RepoRoot, "docs", "scope.md"));

        // The anchor the message now names has to be a real anchor in that file.
        Assert.Contains("<a id=\"object-metadata-system-table\"></a>", limitations, StringComparison.Ordinal);

        // And the reason the old link was wrong has to stay true, or the fix is moot.
        Assert.DoesNotContain("object-metadata", scope, StringComparison.OrdinalIgnoreCase);
    }

    // ── The claim: in scope, not yet answerable for this shape ───────────────────────────

    [Fact]
    public void Refusal_ClaimsNotYetImplemented_NotAPermanentScopeBoundary()
    {
        var ex = Refusal();

        Assert.Equal(Api, ex.Api);
        Assert.StartsWith("not-yet-implemented", ex.Reason, StringComparison.Ordinal);
        Assert.Contains("object-metadata-system-table", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_NamesTheFailingPrecondition_SoTheReaderKnowsWhatMoved()
    {
        var ex = Refusal("SystemTables.ApplicationDatabaseTables not found");

        Assert.Contains("SystemTables.ApplicationDatabaseTables not found", ex.Reason, StringComparison.Ordinal);
        Assert.Contains("SystemTables.ApplicationDatabaseTables not found", ex.Message, StringComparison.Ordinal);
    }

    // ── The consequence: an AL [TryFunction] must NOT read a runner gap as `false` ───────

    [Fact]
    public void Refusal_TearsThroughATryFunction_InsteadOfReadingAsFalse()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw Refusal()));

        Assert.Equal(Api, ex.Api);
    }

    [Fact]
    public void PermanentRefusal_IsStillTrappedByATryFunction_SoTheTestDiscriminatesOnTheReason()
    {
        // Same exception TYPE, different reason: this one really is out of scope forever, and
        // real BC answers `false` there. If this went red alongside the test above, the
        // discrimination would be on the type rather than on the claim.
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw PermanentRefusal()));
    }

    // ── The wire format the expectations manifest and the reporter read ──────────────────

    [Fact]
    public void Refusal_RoundTripsThroughTheOutOfScopeMessageConvention()
    {
        var ex = Refusal("\"Object Type\" carries no option metadata");

        var typed = OutOfScopeMessage.FromException(ex);
        Assert.NotNull(typed);
        Assert.True(typed!.Value.Typed);
        Assert.Equal(Api, typed.Value.Api);
        Assert.Equal(ex.Reason, typed.Value.Reason);

        // The untyped path (message text only) has to recover the same pair — that is what a
        // Cecil-injected throw site and the TRX reader get. The doc link must be stripped and
        // the free-text detail must survive.
        Assert.True(OutOfScopeMessage.TryParse(ex.Message, out var parsed));
        Assert.Equal(Api, parsed.Api);
        Assert.Equal(ex.Reason, parsed.Reason);
        Assert.DoesNotContain("docs/", parsed.Reason, StringComparison.Ordinal);
    }

    // ── The shape cannot drift back: one factory, no direct construction ─────────────────

    [Fact]
    public void EveryRefusalInTheFile_GoesThroughTheOneFactory()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, SourceFile));

        // Comment lines are stripped first: the header explains the history of this defect and
        // has to be able to quote the old wording. The claim under test is about the CODE.
        var code = string.Join('\n', src.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("new RunnerOutOfScopeException(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/scope.md", code, StringComparison.Ordinal);

        // 12 call sites + the factory declaration. A new refusal added without the factory
        // would fail the two assertions above; this one catches a refusal being DELETED
        // silently, which would mean a precondition went back to being read as a default.
        var uses = src.Split("ObjectMetadataShapeGap(").Length - 1;
        Assert.Equal(13, uses);
    }

    // ── The mechanism: a docAnchor may name its own doc file (backward compatible) ───────

    [Fact]
    public void DocAnchorNamingItsOwnFile_IsUsedVerbatim()
    {
        var ex = new RunnerOutOfScopeException("Some.Api", "some-reason", "docs/limitations.md#somewhere");

        Assert.EndsWith(" — see docs/limitations.md#somewhere", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("email", "docs/scope.md#email")]
    [InlineData("#email", "docs/scope.md#email")]
    [InlineData(null, "docs/scope.md")]
    public void BareAnchor_StillResolvesAgainstScopeMd(string? anchor, string expectedLink)
    {
        var ex = new RunnerOutOfScopeException("NavEmail.Send", "email-smtp", anchor);

        Assert.EndsWith(" — see " + expectedLink, ex.Message, StringComparison.Ordinal);
    }
}
