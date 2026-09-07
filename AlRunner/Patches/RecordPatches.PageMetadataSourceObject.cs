// RecordPatches.PageMetadataSourceObject — the nine "Page Metadata" (2000000138) columns
// that come off a page's <SourceObject>, read from BC's OWN parsed metadata (#3063).
//
// ── THE DEFECT ───────────────────────────────────────────────────────────────────────────
//   BuildPageMetadataValue's switch answered 11 columns by name and let every other one
//   fall through to NavValue.GetDefaultNavValue. Nine of the fall-throughs are values the
//   page genuinely declares, so AL reading them got a plausible wrong answer rather than a
//   refusal — the worst class here, because nothing looks broken. Measured on a
//   source-compiled page declaring all of them (issue #3063 has the AL):
//
//     LinksAllowed=No ShowFilter=No SaveValues=No PopulateAllFields=No DataCaptionFields=[]
//     AutoSplitKey=No DelayedInsert=No MultipleNewLines=No SourceTableView=[]
//
//   Six of those nine readings are wrong rather than merely absent; LinksAllowed and
//   ShowFilter read right only because that fixture happens to declare `false`.
//
// ── WHERE THE VALUES COME FROM, AND WHY NOT FROM THE TWO OBVIOUS PLACES ──────────────────
//   The issue proposed feeding these from the two row sources EnumerateKnownPageMetadata
//   already walks — ParsedPage for source-compiled pages, BcAppSymbolCache.PageSymbol for
//   pages in a precompiled dependency .app. That is not what this does, and the reason is
//   the failure mode the issue itself names: "the table starts answering differently
//   depending on where a page came from — which is worse than answering BC's default
//   uniformly."
//
//   Feeding two independent parsers into one column set makes that outcome the DEFAULT.
//   ParsedPage carries none of the nine (measured: RecordPatches.AlPageParser.cs mentions
//   not one of them), so the source-parsed half would have to grow nine new AL-property
//   parsers whose agreement with the symbol-file half is asserted by nobody. Worse, one of
//   the nine cannot be parsed from AL source into the shape BC reports at all without a
//   second resolution step: AL states `DataCaptionFields = "No.", Descr` — field NAMES —
//   where Page Metadata reports field NUMBERS.
//
//   There is a third source that is neither of those and is strictly better than both: BC's
//   OWN MetaSourceObjectDefinition, the object BC's real PageDataProvider reads these nine
//   columns off on a service tier. Both page origins already converge on it:
//
//     * a source-compiled page — the REAL AL compiler emits the metadata XML, captured into
//       AlPageMetadataRegistry, and it has already done the name→number resolution:
//       `DataCaptionFields = "No.", Descr` on a page over table 65600 emits
//       <SourceObject DataCaptionFields="1,2" ...>. Verified with
//       AL_RUNNER_TRACE_PAGE_METADATA=2 on this machine.
//     * a page in a precompiled dependency — DependencyPageMetadataXml reconstructs the same
//       <SourceObject> element from SymbolReference.json (#2820/#2860 put all nine there).
//
//   EnsureRealPageMetadata (RecordPatches.RealPageMetadata.cs) already loads either one
//   through BC's own NCLMetaForm.LoadMetadata(), and BC's own deserializer turns it into the
//   MetaSourceObjectDefinition below. So this file parses nothing: it asks BC for the object
//   BC would have read, and hands out what is on it. The two origins cannot disagree about
//   these nine columns, because after the load there is only one object.
//
// ── SourceTableView IS BC'S OWN FORMATTER, NOT A REIMPLEMENTATION ────────────────────────
//   Field 15 is not the AL text. BC formats it — SORTING(...) ORDER(1) WHERE(Field3=CONST(X))
//   — with field NUMBERS, the filter type's own ToString(), and three conditional segments.
//   PageDataProvider.GenerateSourceTableViewString is `public static` and is the exact method
//   BC's provider calls, so it is invoked here rather than reimplemented. Every detail of
//   that format (when SORTING appears, that ORDER carries the literal '1', the comma join)
//   is then correct by construction instead of by a matching guess.
//
// ── WHAT IS REFUSED RATHER THAN DEFAULTED (loud-failures.md) ─────────────────────────────
//   A page whose real metadata will not load has no MetaSourceObjectDefinition to read, and
//   this file will NOT answer BC's default for it — that is the defect, one level down.
//   PageMetadataSourceObjectGap names the page and the column. See TryGetPageSourceObject
//   for the one case that is genuinely an answer rather than a gap: a page that declares no
//   SourceTable at all has no <SourceObject>, and BC's own provider answers `?? false` /
//   NavText.Empty for exactly that case — a null definition there is BC's answer, not ours.
//
// ── PRECOMPILED-DLL RESPECT ──────────────────────────────────────────────────────────────
//   Runtime-engine and metadata types only (NCLMetaForm, MetaPageDefinition,
//   MetaSourceObjectDefinition, PageDataProvider's own public static formatter). No AL
//   business-logic body is touched, and nothing here re-implements a BC behaviour that BC
//   itself is present to perform.
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// A refusal for a Page Metadata column this file could not answer from BC's own parsed
    /// metadata. Category (2) of RecordPatches.VirtualTableShapeGap.cs — in scope, real BC
    /// answers it, the runner could not — so it carries the "not-yet-implemented" anchor and
    /// an AL <c>[TryFunction]</c> does not trap it into <c>false</c>.
    /// </summary>
    internal static RunnerOutOfScopeException PageMetadataSourceObjectGap(int pageId, string column, string detail)
        => PageMetadataShapeGap(
            $"page {pageId} column \"{column}\": {detail} — refusing rather than answering "
            + "BC's default, which would be a plausible wrong value for a property the page declares");

    /// <summary>
    /// The nine <c>&lt;SourceObject&gt;</c>-derived Page Metadata columns for one page, in the
    /// representation the column carries — already formatted for
    /// <see cref="PageSourceObjectInfo.SourceTableView"/>, already field NUMBERS for
    /// <see cref="PageSourceObjectInfo.DataCaptionFields"/>.
    ///
    /// <para><see cref="Declared"/> is false for a page that declares no <c>SourceTable</c>:
    /// such a page has no <c>&lt;SourceObject&gt;</c> element, and every value below is then
    /// the same one BC's own provider produces from its <c>?? false</c> / <c>NavText.Empty</c>
    /// arms. That is a real answer, not a gap — see <see cref="TryGetPageSourceObject"/>.</para>
    /// </summary>
    internal sealed record PageSourceObjectInfo(
        bool Declared,
        string SourceTableView,
        bool DelayedInsert,
        bool ShowFilter,
        bool MultipleNewLines,
        bool SaveValues,
        bool AutoSplitKey,
        string DataCaptionFields,
        bool LinksAllowed,
        bool PopulateAllFields)
    {
        /// <summary>What BC's own PageDataProvider produces for a page with no
        /// <c>&lt;SourceObject&gt;</c> at all: every boolean <c>?? false</c>, both text
        /// columns <c>NavText.Empty</c>.</summary>
        internal static readonly PageSourceObjectInfo None =
            new(false, string.Empty, false, false, false, false, false, string.Empty, false, false);
    }

    // Resolved once per process. PageDataProvider is `internal` in Ncl.dll, so its public
    // static formatter is reached by reflection rather than by a direct call — the type, not
    // the method, is what is inaccessible.
    private static MethodInfo? _pmvGenerateSourceTableViewString;
    private static bool _pmvViewFormatterResolved;
    private static readonly object _pmvViewFormatterLock = new();

    /// <summary>
    /// BC's own <c>PageDataProvider.GenerateSourceTableViewString(MetaViewDefinition)</c> —
    /// the method BC's real provider formats field 15 with. Null when the shape moved, which
    /// <see cref="ReadSourceTableView"/> turns into a refusal rather than an empty string.
    /// </summary>
    private static MethodInfo? ResolveSourceTableViewFormatter()
    {
        if (_pmvViewFormatterResolved) return _pmvGenerateSourceTableViewString;
        lock (_pmvViewFormatterLock)
        {
            if (_pmvViewFormatterResolved) return _pmvGenerateSourceTableViewString;
            var type = typeof(NCLMetadata).Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.PageDataProvider");
            // BcShape.FindMethod, not Type.GetMethod (#3069). A bare-name lookup throws
            // AmbiguousMatchException the day Microsoft ships a second overload of this name,
            // and MethodScopePatches.NavMethodScope_AssertError rethrows only
            // BcShapeGapException and absorbs everything else — so that throw would be
            // SWALLOWED under an AL `asserterror`, passing it on a call real BC performs fine.
            // FindMethod refuses with the right type instead, and still answers null on
            // absence, which ReadSourceTableView already turns into a named refusal.
            _pmvGenerateSourceTableViewString = type == null ? null : BcShape.FindMethod(
                type, "GenerateSourceTableViewString",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                "Page Metadata (virtual table 2000000138)",
                "PageDataProvider.GenerateSourceTableViewString",
                "it is how BC renders the SourceTableView column (field 15), so the runner "
                + "cannot pick an overload on the page's behalf",
                new[] { typeof(MetaViewDefinition) });
            _pmvViewFormatterResolved = true;
            return _pmvGenerateSourceTableViewString;
        }
    }

    /// <summary>
    /// Format <paramref name="view"/> exactly as BC's own provider formats field 15.
    /// A null view is BC's own <c>NavText.Empty</c> arm (its formatter returns empty for a
    /// null view or a null Sorting), so it needs no refusal; a MISSING FORMATTER does, because
    /// answering an empty string then would be indistinguishable from "this page declares no
    /// view" for a page that declares one.
    /// </summary>
    private static string ReadSourceTableView(object? view, int pageId)
    {
        if (view == null) return string.Empty;
        var formatter = ResolveSourceTableViewFormatter()
            ?? throw PageMetadataSourceObjectGap(
                pageId, "SourceTableView",
                "BC's own PageDataProvider.GenerateSourceTableViewString could not be resolved");
        return formatter.Invoke(null, new[] { view }) as string ?? string.Empty;
    }

    /// <summary>
    /// The nine <c>&lt;SourceObject&gt;</c> columns for <paramref name="pageId"/>, read off
    /// BC's own parsed page metadata — the same <c>MetaSourceObjectDefinition</c> BC's real
    /// <c>PageDataProvider</c> reads them off.
    ///
    /// <para>Throws rather than defaulting when the page's real metadata will not load, since
    /// a default here is a plausible wrong answer for a property the page declares. Returns
    /// <see cref="PageSourceObjectInfo.None"/> — a real answer — for a page whose metadata
    /// loads and states no <c>&lt;SourceObject&gt;</c>, which is what a page declaring no
    /// SourceTable looks like and what BC itself answers <c>false</c>/empty for.</para>
    /// </summary>
    internal static PageSourceObjectInfo GetPageSourceObject(int pageId)
    {
        var meta = EnsureRealPageMetadata(pageId)
            ?? throw PageMetadataSourceObjectGap(
                pageId, "SourceObject properties",
                "the runner has no loadable page metadata for this page, so its declared "
                + "SourceTableView/DelayedInsert/ShowFilter/MultipleNewLines/SaveValues/"
                + "AutoSplitKey/DataCaptionFields/LinksAllowed/PopulateAllFields cannot be read");

        return TryGetPageSourceObject(meta, pageId);
    }

    /// <summary>
    /// Read the nine columns off an already-loaded <c>NCLMetaForm</c>. Split out from
    /// <see cref="GetPageSourceObject"/> so the metadata-load half and the read half fail
    /// separately and say which of the two failed.
    /// </summary>
    private static PageSourceObjectInfo TryGetPageSourceObject(object meta, int pageId)
    {
        MetaPageDefinition? definition;
        try
        {
            definition = (meta as NCLMetaForm)
                ?.GetFrozenPageDefinitionWithExtensionWithoutMergedMultiLanguage().Item;
        }
        catch (Exception ex)
        {
            throw PageMetadataSourceObjectGap(
                pageId, "SourceObject properties",
                $"BC's own page definition could not be read ({ex.GetType().Name}: {ex.Message})");
        }

        if (definition == null)
            throw PageMetadataSourceObjectGap(
                pageId, "SourceObject properties",
                "BC's own page definition is null even though the page's metadata loaded");

        // A page that declares no SourceTable carries no <SourceObject>, and BC's own provider
        // reads every one of these nine through `metaSourceObjectDefinition?.X ?? false` (or
        // NavText.Empty). So null here is BC's ANSWER, not a gap — the one case in this file
        // that is defaulted rather than refused, and it is defaulted to exactly what BC
        // produces for it.
        var sourceObject = definition.Properties?.SourceObject;
        if (sourceObject == null) return PageSourceObjectInfo.None;

        return new PageSourceObjectInfo(
            Declared: true,
            SourceTableView: ReadSourceTableView(sourceObject.SourceTableView, pageId),
            DelayedInsert: sourceObject.DelayedInsert,
            ShowFilter: sourceObject.ShowFilter,
            MultipleNewLines: sourceObject.MultipleNewLines,
            SaveValues: sourceObject.SaveValues,
            AutoSplitKey: sourceObject.AutoSplitKey,
            // BC reads this straight through as text; the AL compiler and the symbol file both
            // already state it as the comma-separated FIELD NUMBER list the column carries, so
            // there is nothing to resolve here (DependencyPageMetadataXml.IsFieldNumberList
            // holds the symbol-file half to that shape before it ever reaches this point).
            DataCaptionFields: sourceObject.DataCaptionFields ?? string.Empty,
            LinksAllowed: sourceObject.LinksAllowed,
            PopulateAllFields: sourceObject.PopulateAllFields);
    }
}
