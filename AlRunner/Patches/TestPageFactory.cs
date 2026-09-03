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

        // Lazy trigger/validate-subscriber injection (issue #2411, mirroring #2197/#2412's fix
        // at the three sites RecordPatches.CreateObjectInstance / RecordPatches
        // .CreateObjectInstance.cs / NavRecordRefPatches.RecordRef.Open). Called AFTER
        // GetOrBuildNCLMetaTable returns, never from inside BuildNCLMetaTable itself — see the
        // reentrancy NOTE on InjectTriggerSubsForTable.
        //
        // MEASURED (stack trace, issue #2411 investigation): on every path this method's
        // caller can actually reach a live page object through, BC's own SetSourceTable ->
        // EnsureMetadataLoaded -> InitializeFromMetadata, and separately NewRecordAsync ->
        // RaiseOnNewRecordAsync -> NavRecord.get_OldRecord, already construct this table's
        // xRec via NCLMetaTable.CreateObjectInstance (RecordPatches.CreateObjectInstance.cs) --
        // one of the three #2412-fixed sites -- before OnBeforeInsertEvent/OnBeforeDeleteEvent
        // can fire on `record` itself. That FAITHFULLY masks the gap end-to-end for a driven
        // TestPage or Page-variable with a compiled page object: the subscriber is wired by the
        // time Insert/Modify/Delete/Rename runs, just via xRec rather than via `record`
        // directly. The call below still matters for RunnerPageInstance.TryCreate's record-only
        // fallback (no page object built at all -- see its catch block, and the "no
        // source-expression table" branch) where neither of those BC call chains ever runs, and
        // it keeps this site's contract identical to the other three regardless of which
        // fallback a future caller takes.
        AlRunner.Patches.EventSubscriberPatches.InjectValidateSubsForTable(tableId, metaTable);
        AlRunner.Patches.EventSubscriberPatches.InjectTriggerSubsForTable(tableId, metaTable);

        var ctor = recordType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 6);
        if (ctor == null)
        {
            why = $"Record{tableId} has no 6-arg constructor";
            return null;
        }

        var record = (NavRecord)ctor.Invoke(new object?[]
        {
            owner, metaTable, isTemporary, null, null, SecurityFiltering.Ignored
        });

        // Register tableextensions on THIS record instance, same as the other three
        // record-construction sites (RecordPatches.CreateObjectInstance.cs,
        // NavRecordRefPatches.cs, RecordPatches.cs's NavRecordHandle.CreateTarget) — issue
        // #2490: a TestPage's own Rec is built here, directly via the concrete Record{Id}
        // ctor, bypassing NCLMetaTable.CreateObjectInstance entirely, so without this call an
        // extension field's OnValidate trigger (wired at the metatable/field level by
        // WireFieldTriggerHandlersForTable/WireExtensionValidateHandlers, HandlerType =
        // TableExtension{extId}) had no registered extension instance of that type to dispatch
        // to on THIS record, and BC's own InvokeFieldTriggerHandlerAsync fell back to casting
        // the record itself — an InvalidCastException naming the base Record{tableId} type
        // and the extension type it could not find.
        RecordPatches.RegisterParsedTableExtensions(record, tableId);

        return record;
    }
}
