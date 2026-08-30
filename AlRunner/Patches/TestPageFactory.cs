// TestPageFactory — build the record + live AL page object behind a TestPage.
//
// Extracted from CodeunitPatches.CreateTestPageClient once a second caller appeared: a
// subpage PART is just another page, over its own source table, driven the same way. The
// only difference is who supplies the record filter (a part's is the SubPageLink) and which
// wrapper class the result goes into.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

internal static class TestPageFactory
{
    /// <summary>What a live TestPage needs: a record cursor over the page's source table
    /// and, where the runner compiled the page itself, the AL page object behind it.</summary>
    internal sealed record Built(NavRecord Record, RunnerPageInstance? Page, int TableId);

    /// <summary>
    /// Build the record (and, where possible, the AL page object) for <paramref name="pageId"/>.
    /// Returns null with <paramref name="why"/> set when the page cannot be driven live —
    /// the caller decides whether that is a graceful degradation or a loud refusal.
    /// </summary>
    internal static Built? TryBuild(object owner, int pageId, out string? why)
    {
        why = null;

        // Opt the page into a real metadata load — its parsed control tree, which is what a
        // control bound to a page VARIABLE (rather than to a Rec field) resolves through.
        RecordPatches.EnsureRealPageMetadata(pageId);

        // GetSourceTableIdForPage only knows pages the runner AL-source-parsed itself; a
        // precompiled dependency's page (Base App / System App / an ISV .app) falls back to
        // its SymbolReference.json's own SourceTable property — see
        // RecordPatches.TryGetDependencySourceTableIdForPage.
        var tableId = RecordPatches.GetSourceTableIdForPage(pageId);
        if (tableId == 0)
            tableId = RecordPatches.TryGetDependencySourceTableIdForPage(pageId);
        if (tableId == 0)
        {
            why = $"page {pageId} declares no SourceTable";
            return null;
        }

        // isTemporary: false — TestPage over a temporary-source-table page is not this
        // path's concern today; only the plain-page-variable caller below currently needs
        // it (issue #1719's Page 700 "Error Messages"), and changing this one's shape is
        // out of scope for that fix.
        var record = TryBuildBlankRecord(owner, tableId, isTemporary: false, out var recordWhy);
        if (record == null)
        {
            why = $"page {pageId}: {recordWhy}";
            return null;
        }

        return new Built(record, RunnerPageInstance.TryCreate(owner, pageId, record), tableId);
    }

    /// <summary>
    /// The AL page object for a page that declares NO SourceTable, or null when the runner
    /// has nothing to build one from.
    ///
    /// A page with no SourceTable is ordinary, legal AL — the StandardDialog / Worksheet
    /// header shape, whose controls bind to page globals rather than to a record. It is NOT
    /// a page that "cannot be driven live": it has a control tree, a part list and its own
    /// AL triggers, and everything except record access works on it. Deciding otherwise is
    /// what made a subpage part on such a host answer an empty rowset under a directly
    /// opened TestPage (issue #2090).
    ///
    /// <para>Deliberately narrow: the caller must have established that the page genuinely
    /// declares no SourceTable (<c>RecordPatches.PageDeclaresSourceTable</c>). The OTHER
    /// ways <see cref="TryBuild"/> returns null — a declared source table whose runtime
    /// record type is missing, a page the parser never saw — are runner gaps, and answering
    /// them with a record-less page would swap one wrong answer for another.</para>
    /// </summary>
    internal static RunnerPageInstance? TryBuildRecordless(object owner, int pageId)
    {
        RecordPatches.EnsureRealPageMetadata(pageId);
        return RunnerPageInstance.TryCreateRecordless(owner, pageId);
    }

    /// <summary>
    /// A blank (unpositioned) cursor over <paramref name="tableId"/>, owned by
    /// <paramref name="owner"/> — the same shape BC's real page construction binds Rec to
    /// before any row is read. Shared by the TestPage path above and by
    /// <c>CodeunitPatches.NavFormHandle_CreateTarget</c>, which needs the identical record
    /// to bind a plain <c>Page X</c> variable's Rec (see issue #1719: a page variable built
    /// via the single-arg ctor never gets one, so any Base App page method reading Rec NREs
    /// before AL ever runs).
    /// <para><paramref name="isTemporary"/> must match the page's own
    /// <c>SourceTableTemporary</c> declaration — Page 700 "Error Messages" declares it
    /// true, and its SetRecords body does <c>Rec.Copy(TempErrorMessage, true)</c>, which
    /// real BC's Copy(shareTable: true) refuses unless BOTH records are temporary
    /// ("The COPY function can only be used with the shareTable argument set to true if
    /// both records are temporary").</para>
    /// </summary>
    internal static NavRecord? TryBuildBlankRecord(object owner, int tableId, bool isTemporary, out string? why)
    {
        why = null;
        var metaTable = RecordPatches.GetOrBuildNCLMetaTable(tableId);
        var recordType = RecordPatches.FindRecordType(tableId);
        if (metaTable == null || recordType == null)
        {
            why = $"source table {tableId} has no runtime record type here";
            return null;
        }

        var ctor = recordType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 6);
        if (ctor == null)
        {
            why = $"Record{tableId} has no 6-arg constructor";
            return null;
        }

        return (NavRecord)ctor.Invoke(new object?[]
        {
            owner, metaTable, isTemporary, null, null, SecurityFiltering.Ignored
        });
    }
}
