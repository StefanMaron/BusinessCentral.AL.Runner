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

        var tableId = RecordPatches.GetSourceTableIdForPage(pageId);
        if (tableId == 0)
        {
            why = $"page {pageId} declares no SourceTable";
            return null;
        }

        var metaTable = RecordPatches.GetOrBuildNCLMetaTable(tableId);
        var recordType = RecordPatches.FindRecordType(tableId);
        if (metaTable == null || recordType == null)
        {
            why = $"page {pageId}: source table {tableId} has no runtime record type here";
            return null;
        }

        var ctor = recordType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 6);
        if (ctor == null)
        {
            why = $"page {pageId}: Record{tableId} has no 6-arg constructor";
            return null;
        }

        var record = (NavRecord)ctor.Invoke(new object?[]
        {
            owner, metaTable, false, null, null, SecurityFiltering.Ignored
        });

        return new Built(record, RunnerPageInstance.TryCreate(owner, pageId, record), tableId);
    }
}
