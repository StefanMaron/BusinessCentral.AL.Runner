// RecordPatches.DependencyPageMetadata — SourceTable lookup for pages that live in a
// PRECOMPILED dependency .app, which the runner never source-compiles.
//
// THE GAP (issue #1719)
//   NavFormHandle.CreateTarget (a plain `Page X` variable, as opposed to a TestPage) needs
//   to bind Rec to a real record of the page's own SourceTable before handing the instance
//   to AL — otherwise any Base App/System App page method that reads Rec (e.g. Page 700
//   "Error Messages".SetRecords: `Rec.Copy(TempErrorMessage, true)`) NREs before AL ever
//   runs. RecordPatches.GetSourceTableIdForPage answers that ONLY for a page the runner
//   AL-source-parsed itself (AlPageParser scans `_sourceDirs`, which is the bundle's own
//   .al files) — a precompiled dependency's page has no entry there at all.
//
// WHAT IS RECONSTRUCTED, AND FROM WHAT
//   The dependency .app's own SymbolReference.json states the page's SourceTable property
//   verbatim as the table's numeric ID (see BcAppSymbolCache.TryParsePageSymbol) — this is
//   the same file DependencyReportMetadata already reads for a dependency report's dataset
//   shape, so nothing new is inferred here, only a second typed slice of the same source.
namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// A precompiled dependency's SourceTable table id for <paramref name="pageId"/>, or 0
    /// when no loaded dependency .app describes that page (or the page declares no
    /// SourceTable at all — a legal AL page with no bound record).
    /// </summary>
    internal static int TryGetDependencySourceTableIdForPage(int pageId)
        => TryGetDependencyPageSymbol(pageId)?.SourceTableId ?? 0;

    /// <summary>
    /// <paramref name="pageId"/>'s SourceTable table id, checking the runner's own
    /// AL-source-parsed pages first, then any loaded dependency .app's SymbolReference.json.
    /// 0 when neither knows the page or the page declares no SourceTable.
    /// </summary>
    internal static int ResolveSourceTableIdForAnyPage(int pageId)
    {
        var tableId = GetSourceTableIdForPage(pageId);
        return tableId != 0 ? tableId : TryGetDependencySourceTableIdForPage(pageId);
    }

    /// <summary>
    /// Whether <paramref name="pageId"/> declares <c>SourceTableTemporary = true</c> —
    /// checking the runner's own AL-source-parsed pages first, then any loaded dependency
    /// .app. False (including "unknown page") is the safe default — it is also AL's own
    /// default, so a page the runner cannot find gets exactly the record shape a page with
    /// no such declaration would. See issue #1719: Page 700 "Error Messages" declares it
    /// true, and its own SetRecords body's <c>Rec.Copy(TempErrorMessage, true)</c> requires
    /// a temporary Rec to match.
    /// </summary>
    internal static bool ResolveSourceTableTemporaryForAnyPage(int pageId)
        => (IsPageParsed(pageId) && _parsedPages.TryGetValue(pageId, out var page) && page.SourceTableTemporary)
           || TryGetDependencyPageSymbol(pageId)?.SourceTableTemporary == true;

    private static BcAppSymbolCache.PageSymbol? TryGetDependencyPageSymbol(int pageId)
    {
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            List<BcAppSymbolCache.PageSymbol> pages;
            try
            {
                pages = BcAppSymbolCache.Get(appPath).Pages;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] dependency page metadata: SymbolReference read failed for "
                    + $"{Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var p in pages)
                if (p.Id == pageId)
                    return p;
        }
        return null;
    }
}
