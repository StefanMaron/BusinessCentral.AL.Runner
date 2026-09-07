// PageMetadataSourceObjectRefusalTests — pins the runner-only half of issue #3063:
// what RecordPatches.GetPageSourceObject does when it CANNOT read a page's <SourceObject>.
//
// What this proves, and what it deliberately does NOT
// -----------------------------------------------------
// The BC-observable claim — that Page Metadata (2000000138) reports a page's declared
// SourceTableView / DelayedInsert / ShowFilter / MultipleNewLines / SaveValues /
// AutoSplitKey / DataCaptionFields / LinksAllowed / PopulateAllFields rather than the
// column's type default — is plain BC behaviour and belongs upstream against a real service
// tier (.claude/rules/bc-behavior-tests-go-upstream.md). It is proved there, in corpus
// codeunit 60993 "Test Page Metadata Src Object".
//
// This file pins the narrower runner-only claim underneath it, which no service tier can
// adjudicate because real BC never lacks the metadata: when the runner has no loadable page
// metadata for a page, the nine columns must REFUSE rather than quietly answer BC's default.
// That distinction is the whole point of the issue — the defect being fixed was a silent
// wrong answer, and replacing it with a *different* silent wrong answer would be no fix at
// all (.claude/rules/loud-failures.md).
//
// The refusal also has to carry the "not-yet-implemented" anchor, or
// ApplicationObjectBasePatches.IsPermanentOutOfScope classifies it as permanently out of
// scope and an AL [TryFunction] traps it into `false` — turning the loud failure back into
// the silent one, one layer up. That is asserted, not assumed.

using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class PageMetadataSourceObjectRefusalTests
{
    // An id no fixture, no dependency .app and no corpus page declares, so
    // EnsureRealPageMetadata genuinely has nothing to load for it. Deliberately far from the
    // 88123xxx block DependencyPageMetadataXmlTests uses and from every 6xxxx corpus range.
    private const int PageNoRunnerMetadataFor = 77451903;

    [Fact]
    public void GetPageSourceObject_PageWithNoLoadableMetadata_ThrowsInsteadOfAnsweringBcDefaults()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.GetPageSourceObject(PageNoRunnerMetadataFor));

        // The page has to be named, or the refusal cannot be acted on: "some page's
        // SourceObject could not be read" is not a diagnosis.
        Assert.Contains(PageNoRunnerMetadataFor.ToString(), ex.Message);

        // The refusal must say the surface is unimplemented, NOT permanently out of scope.
        // IsPermanentOutOfScope treats any reason that does not start with
        // "not-yet-implemented" as permanent, and an AL [TryFunction] over a permanent
        // refusal yields `false` — which would restore the silent wrong answer this whole
        // change removes.
        Assert.StartsWith("not-yet-implemented", ex.Reason);
        Assert.Contains("page-metadata-virtual-table", ex.Reason);

        // And it must name what could not be answered, so the reader knows which columns are
        // affected rather than only that something failed.
        Assert.Contains("SourceTableView", ex.Message);
        Assert.Contains("DataCaptionFields", ex.Message);
    }

    [Fact]
    public void PageSourceObjectInfoNone_MatchesWhatBcsOwnProviderAnswersForAPageWithNoSourceObject()
    {
        // A page declaring no SourceTable carries no <SourceObject> element at all, and BC's
        // own PageDataProvider reads all nine columns off it as
        // `metaSourceObjectDefinition?.X ?? false` (or NavText.Empty for the two text
        // columns). So THAT case is a real answer rather than a gap, and this pins the value
        // it answers with — the one place in this change where a default is legitimate, so
        // the one place it must be stated explicitly rather than fallen into.
        var none = RecordPatches.PageSourceObjectInfo.None;

        Assert.False(none.Declared);
        Assert.Equal(string.Empty, none.SourceTableView);
        Assert.Equal(string.Empty, none.DataCaptionFields);
        Assert.False(none.DelayedInsert);
        Assert.False(none.ShowFilter);
        Assert.False(none.MultipleNewLines);
        Assert.False(none.SaveValues);
        Assert.False(none.AutoSplitKey);
        Assert.False(none.LinksAllowed);
        Assert.False(none.PopulateAllFields);
    }
}
