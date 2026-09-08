// PageMetadataPropertiesTests — pins the runner-only half of issue #3601: what
// RecordPatches.GetPageProperties/FormatInherentMask do independently of any single page's
// declared values.
//
// What this proves, and what it deliberately does NOT
// -----------------------------------------------------
// The BC-observable claim — that Page Metadata (2000000138) reports a page's declared
// RefreshOnActivate/APIPublisher/APIGroup/APIVersion/EntitySetName/EntityName/
// DataCaptionExpr./ChangeTrackingAllowed/InherentPermissions/InherentEntitlements/AL
// Namespace rather than each column's type default — is plain BC behaviour and belongs
// upstream against a real service tier (.claude/rules/bc-behavior-tests-go-upstream.md). It
// is proved there, in corpus codeunit 60904 "Test Page Metadata Properties" (corpus PR #298,
// merged, all eight cloud legs green — run 34275562528).
//
// This file pins two narrower runner-only claims underneath it:
//
//   1. When the runner has no loadable page metadata for a page, GetPageProperties must
//      REFUSE rather than quietly answer BC's default — same policy, and same shape of
//      test, as PageMetadataSourceObjectRefusalTests pins for the sibling <SourceObject>
//      columns.
//   2. FormatInherentMask actually reaches BC's own runtime-engine
//      PermissionDefinition.ExpandDirectPermissionToIndirect and
//      MetadataDataProvider.CreatePermissionMaskString and renders their result correctly.
//      No service tier can adjudicate THIS — it is a question about whether this runner
//      BUILD's reflection resolves those two BC members and calls them in the right order,
//      not about what a page's declared mask means.

using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class PageMetadataPropertiesTests
{
    // An id no fixture, no dependency .app and no corpus page declares, so
    // EnsureRealPageMetadata genuinely has nothing to load for it. Deliberately distinct from
    // PageMetadataSourceObjectRefusalTests' own PageNoRunnerMetadataFor (77451903), and far
    // from the 88123xxx block DependencyPageMetadataXmlTests uses and from every 6xxxx corpus
    // range.
    private const int PageNoRunnerMetadataFor = 77451904;

    [Fact]
    public void GetPageProperties_PageWithNoLoadableMetadata_ThrowsInsteadOfAnsweringBcDefaults()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.GetPageProperties(PageNoRunnerMetadataFor));

        // The page has to be named, or the refusal cannot be acted on.
        Assert.Contains(PageNoRunnerMetadataFor.ToString(), ex.Message);

        // The refusal must say the surface is unimplemented, NOT permanently out of scope —
        // ApplicationObjectBasePatches.IsPermanentOutOfScope treats any reason that does not
        // start with "not-yet-implemented" as permanent, and an AL [TryFunction] over a
        // permanent refusal yields `false`, restoring the silent wrong answer this change
        // removes.
        Assert.StartsWith("not-yet-implemented", ex.Reason);
        Assert.Contains("page-metadata-virtual-table", ex.Reason);

        // And it must name enough of the eleven columns that the reader knows the whole
        // group is affected, not just one.
        Assert.Contains("RefreshOnActivate", ex.Message);
        Assert.Contains("InherentPermissions", ex.Message);
        Assert.Contains("InherentEntitlements", ex.Message);
        Assert.Contains("AL Namespace", ex.Message);
    }

    [Theory]
    // PermissionMask.None formats to NavText.Empty — the one legitimate default in this
    // file, for a page declaring neither InherentPermissions nor InherentEntitlements.
    [InlineData(0, "")]
    // PermissionMask.Execute (bit 4, value 16) is the ONLY value a page may ever declare —
    // the AL compiler rejects anything else on a page with AL0195 (see
    // docs/page-metadata-properties.md). Formats to the single uppercase letter "X".
    [InlineData(16, "X")]
    // A multi-bit mask a TABLE can declare (Read|Insert|Modify|Delete|Execute = 1+2+4+8+16),
    // which no page can produce but which the SAME reflection call must still render
    // correctly, since FormatInherentMask does not know or care which object kind called it.
    // Proves the wiring reaches BC's real formatter rather than a page-specific shortcut.
    [InlineData(31, "RIMDX")]
    public void FormatInherentMask_ReachesBcsOwnFormatterAndRendersItsLetterCode(int declaredMask, string expected)
    {
        var actual = RecordPatches.FormatInherentMask(declaredMask, pageId: 1, column: "InherentPermissions");
        Assert.Equal(expected, actual);
    }
}
