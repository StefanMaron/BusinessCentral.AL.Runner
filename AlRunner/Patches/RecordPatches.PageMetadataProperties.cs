// RecordPatches.PageMetadataProperties — the eleven "Page Metadata" (2000000138) columns
// that come off a page's <Properties> element (ten of them) or the page definition itself
// (ALNamespace), read from BC's OWN parsed metadata (#3601).
//
// ── THE DEFECT ───────────────────────────────────────────────────────────────────────────
//   BuildPageMetadataValue's switch fell through eleven of its columns to
//   NavValue.GetDefaultNavValue — a hardcoded default, never the page's own declaration.
//   Measured at main 9a0dd368 (issue #3601): eleven of the twelve defaulted columns are
//   stated outright in BC's own emitted <Properties> document — RefreshOnActivate in 236 of
//   236 pages, ALNamespace in 236 of 236, InherentEntitlements in 94, InherentPermissions in
//   92, APIVersion in 39 on Base App, DataCaptionExpr. in 32, EntityName/EntitySetName in 10,
//   APIPublisher/APIGroup in 8, ChangeTrackingAllowed in 2.
//
// ── WHERE THE VALUES COME FROM, AND WHY NOT FROM THE TWO OBVIOUS PLACES ──────────────────
//   #3063 established the pattern one element down, on <SourceObject>: BOTH page origins —
//   a page this run source-compiled, and a page declared by a precompiled dependency .app —
//   already converge on ONE object, BC's own MetaPageDefinition, loaded through
//   EnsureRealPageMetadata's NCLMetaForm.LoadMetadata(). This file reads the same object one
//   layer up: MetaPageDefinition.Properties (a MetaPageProperties) for ten of the eleven, and
//   MetaPageDefinition.ALNamespace itself for the eleventh — verified against BC 28.1's real
//   PageDataProvider.<GetValuesWithinRangeForKeyField>d__3.MoveNext() (decompiled): it reads
//   `properties.RefreshOnActivate` / `.APIPublisher` / `.APIGroup` / `.APIVersion` /
//   `.EntitySetName` / `.EntityName` / `.DataCaptionExpr` / `.ChangeTrackingAllowed` and
//   `item.ALNamespace` (via `GetNormalizedNamespace`, which is `NavText.Create(fieldValue)` —
//   no further transform) directly off this same object, so there is nothing left for either
//   row source (ParsedPage / BcAppSymbolCache.PageSymbol) to add.
//
// ── InherentPermissions / InherentEntitlements ARE NOT A DIRECT FIELD READ ───────────────
//   BC's real column value is NOT `properties.InherentPermissions` printed as text — it is
//   that raw declared mask, expanded and formatted through two of BC's OWN runtime-engine
//   methods, reused here rather than reimplemented (precompiled-dll-respect.md):
//     PermissionDefinition.ExpandDirectPermissionToIndirect(PermissionMask) — sets the
//       matching INDIRECT bit for every DIRECT bit a page declares (NCLMetaForm.LoadMetadata
//       does exactly this before storing InherentPermissionsAndEntitlements);
//     MetadataDataProvider.CreatePermissionMaskString(PermissionMask) — renders the mask as
//       the letters columns 30/31 carry: uppercase for a DIRECT bit, lowercase for an
//       INDIRECT-only one, empty for PermissionMask.None.
//   Both are internal members of internal-but-loadable types in Ncl.dll, resolved by
//   reflection the same way PageDataProvider.GenerateSourceTableViewString is in
//   RecordPatches.PageMetadataSourceObject.cs. A PAGE can only ever declare `X` (Execute) for
//   either property — the AL compiler rejects `rimd` on a page with "Invalid permission kind.
//   Expected: 'X'" — so the expand step never changes the OBSERVABLE letters for a page (a
//   direct bit always wins the uppercase branch before its own indirect twin is checked), but
//   it is still BC's real call graph, not a shortcut that happens to agree with it today.
//
// ── DataCaptionExpr. IS A FIXED PLACEHOLDER, NOT THE AL SOURCE TEXT ──────────────────────
//   Measured against the real AL compiler (BC 28.1): a page declaring
//   `DataCaptionExpression = 'Probe Page Fixture';` and one declaring
//   `DataCaptionExpression = 'Some Other Totally Different Text Value XYZ';` both compile to
//   the identical <Properties DataCaptionExpr="DataCaptionExprCode" .../> — BC compiles the
//   expression to a generated method and the document merely names that there is one, not
//   what it evaluates to. properties.DataCaptionExpr is exactly that fixed string, so reading
//   it verbatim is correct; there is no formatting step to reproduce.
//
// ── WHAT IS REFUSED RATHER THAN DEFAULTED (loud-failures.md) ─────────────────────────────
//   Same policy as the sibling <SourceObject> file: a page whose real metadata will not load
//   gets a named refusal naming the column, never BC's default. Unlike <SourceObject>,
//   MetaPageDefinition.Properties itself is not null for any page shape the compiler
//   produces — verified against every corpus page BC 28.1 compiles — so a null there is
//   refused as a BC-shape gap rather than treated as a real, if unusual, answer.
//
// ── AppID IS DELIBERATELY NOT HERE ────────────────────────────────────────────────────────
//   BC's real column reads MetadataDataProvider.GetAppId(metaFormById), computed from the
//   page's owning app-identity/app-group, not from <Properties> at all — the one column of
//   the twelve the issue's own measurement found unstated on the document. Left on
//   NavValue.GetDefaultNavValue in RecordPatches.PageMetadataVirtualTable.cs, unchanged.
//
// ── PRECOMPILED-DLL RESPECT ──────────────────────────────────────────────────────────────
//   Runtime-engine and metadata types only (NCLMetaForm, MetaPageDefinition,
//   MetaPageProperties, PermissionDefinition, MetadataDataProvider — all reached the same way
//   the sibling <SourceObject> file reaches PageDataProvider). No AL business-logic body is
//   touched, and nothing here re-implements a BC behaviour BC itself is present to perform.

using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// A refusal for a Page Metadata column this file could not answer from BC's own parsed
    /// metadata. Category (2) of RecordPatches.VirtualTableShapeGap.cs — same policy as
    /// <see cref="PageMetadataSourceObjectGap"/>.
    /// </summary>
    internal static RunnerOutOfScopeException PageMetadataPropertiesGap(int pageId, string column, string detail)
        => PageMetadataShapeGap(
            $"page {pageId} column \"{column}\": {detail} — refusing rather than answering "
            + "BC's default, which would be a plausible wrong value for a property the page declares");

    /// <summary>
    /// The eleven <c>&lt;Properties&gt;</c>-derived Page Metadata columns for one page.
    /// <see cref="InherentPermissions"/> and <see cref="InherentEntitlements"/> are already
    /// formatted through BC's own letter-code renderer (empty for a page declaring neither).
    /// </summary>
    internal sealed record PagePropertiesInfo(
        bool RefreshOnActivate,
        string ALNamespace,
        string InherentPermissions,
        string InherentEntitlements,
        string APIVersion,
        string DataCaptionExpr,
        string EntityName,
        string EntitySetName,
        string APIPublisher,
        string APIGroup,
        bool ChangeTrackingAllowed);

    private static MethodInfo? _pmvExpandDirectPermissionToIndirect;
    private static MethodInfo? _pmvCreatePermissionMaskString;
    private static bool _pmvPermissionFormatterResolved;
    private static readonly object _pmvPermissionFormatterLock = new();

    /// <summary>
    /// BC's own <c>PermissionDefinition.ExpandDirectPermissionToIndirect</c> and
    /// <c>MetadataDataProvider.CreatePermissionMaskString</c> — the two methods
    /// <c>NCLMetaForm.LoadMetadata()</c> and BC's real <c>PageDataProvider</c> chain to turn a
    /// page's declared InherentPermissions/InherentEntitlements int into the letter-code text
    /// columns 30/31 carry. Null when either could not be resolved, which
    /// <see cref="FormatInherentMask"/> turns into a refusal rather than a guessed format.
    /// </summary>
    private static (MethodInfo Expand, MethodInfo Format)? ResolvePermissionFormatter()
    {
        if (_pmvPermissionFormatterResolved)
            return _pmvExpandDirectPermissionToIndirect != null && _pmvCreatePermissionMaskString != null
                ? (_pmvExpandDirectPermissionToIndirect, _pmvCreatePermissionMaskString) : null;
        lock (_pmvPermissionFormatterLock)
        {
            if (_pmvPermissionFormatterResolved)
                return _pmvExpandDirectPermissionToIndirect != null && _pmvCreatePermissionMaskString != null
                    ? (_pmvExpandDirectPermissionToIndirect, _pmvCreatePermissionMaskString) : null;

            var nclAssembly = typeof(NCLMetadata).Assembly;
            var permissionDefinitionType = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.PermissionDefinition");
            var metadataDataProviderType = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.MetadataDataProvider");

            _pmvExpandDirectPermissionToIndirect = permissionDefinitionType == null ? null : BcShape.FindMethod(
                permissionDefinitionType, "ExpandDirectPermissionToIndirect",
                BindingFlags.NonPublic | BindingFlags.Static,
                "Page Metadata (virtual table 2000000138)", "PermissionDefinition.ExpandDirectPermissionToIndirect",
                "it expands a page's declared DIRECT InherentPermissions/InherentEntitlements bits to "
                + "their matching INDIRECT bits, exactly as BC's own NCLMetaForm.LoadMetadata does before "
                + "formatting them, so the runner cannot skip it on the page's behalf",
                new[] { typeof(PermissionMask) });

            _pmvCreatePermissionMaskString = metadataDataProviderType == null ? null : BcShape.FindMethod(
                metadataDataProviderType, "CreatePermissionMaskString",
                BindingFlags.Public | BindingFlags.Static,
                "Page Metadata (virtual table 2000000138)", "MetadataDataProvider.CreatePermissionMaskString",
                "it renders a PermissionMask into the letter-code text these two columns carry "
                + "(uppercase direct, lowercase indirect-only), and the runner cannot re-derive that "
                + "case rule on the page's behalf",
                new[] { typeof(PermissionMask) });

            _pmvPermissionFormatterResolved = true;
            return _pmvExpandDirectPermissionToIndirect != null && _pmvCreatePermissionMaskString != null
                ? (_pmvExpandDirectPermissionToIndirect, _pmvCreatePermissionMaskString) : null;
        }
    }

    /// <summary>
    /// <paramref name="declaredMask"/> (a page's raw declared InherentPermissions or
    /// InherentEntitlements int) rendered exactly as BC's own
    /// InherentObjectPermissionAndEntitlementsHelper/MetadataDataProvider chain renders it —
    /// empty for a page declaring neither (PermissionMask.None formats to NavText.Empty).
    /// </summary>
    private static string FormatInherentMask(int declaredMask, int pageId, string column)
    {
        var formatter = ResolvePermissionFormatter()
            ?? throw PageMetadataPropertiesGap(pageId, column,
                "BC's own PermissionDefinition.ExpandDirectPermissionToIndirect or "
                + "MetadataDataProvider.CreatePermissionMaskString could not be resolved");
        try
        {
            var direct = (PermissionMask)checked((ushort)declaredMask);
            var expanded = (PermissionMask)formatter.Expand.Invoke(null, new object?[] { direct })!;
            var formatted = (NavText)formatter.Format.Invoke(null, new object?[] { expanded })!;
            return formatted.Value ?? string.Empty;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            throw PageMetadataPropertiesGap(
                pageId, column,
                $"BC's own permission-mask formatter could not render the declared value {declaredMask} "
                + $"({inner.GetType().Name}: {inner.Message})");
        }
    }

    /// <summary>
    /// The eleven <c>&lt;Properties&gt;</c>-derived columns for <paramref name="pageId"/>,
    /// read off BC's own parsed page metadata — the same <c>MetaPageProperties</c>/
    /// <c>MetaPageDefinition.ALNamespace</c> BC's real <c>PageDataProvider</c> reads them off.
    ///
    /// <para>Throws rather than defaulting when the page's real metadata will not load, since
    /// a default here is a plausible wrong answer for a property the page declares. Unlike
    /// <see cref="GetPageSourceObject"/>'s <c>SourceObject</c>, <c>Properties</c> itself is
    /// never null for a page BC's compiler produces, so a null one is a BC-shape gap rather
    /// than a real answer to default to.</para>
    /// </summary>
    internal static PagePropertiesInfo GetPageProperties(int pageId)
    {
        var meta = EnsureRealPageMetadata(pageId)
            ?? throw PageMetadataPropertiesGap(
                pageId, "Properties",
                "the runner has no loadable page metadata for this page, so its declared "
                + "RefreshOnActivate/APIPublisher/APIGroup/APIVersion/EntitySetName/EntityName/"
                + "DataCaptionExpr./ChangeTrackingAllowed/InherentPermissions/InherentEntitlements/"
                + "AL Namespace cannot be read");

        return TryGetPageProperties(meta, pageId);
    }

    /// <summary>
    /// Read the eleven columns off an already-loaded <c>NCLMetaForm</c>. Split out from
    /// <see cref="GetPageProperties"/> so the metadata-load half and the read half fail
    /// separately and say which of the two failed — same shape as
    /// <see cref="TryGetPageSourceObject"/>.
    /// </summary>
    private static PagePropertiesInfo TryGetPageProperties(object meta, int pageId)
    {
        Microsoft.Dynamics.Nav.Types.Metadata.MetaPageDefinition? definition;
        try
        {
            definition = (meta as NCLMetaForm)
                ?.GetFrozenPageDefinitionWithExtensionWithoutMergedMultiLanguage().Item;
        }
        catch (Exception ex)
        {
            throw PageMetadataPropertiesGap(
                pageId, "Properties",
                $"BC's own page definition could not be read ({ex.GetType().Name}: {ex.Message})");
        }

        if (definition == null)
            throw PageMetadataPropertiesGap(
                pageId, "Properties",
                "BC's own page definition is null even though the page's metadata loaded");

        var properties = definition.Properties
            ?? throw PageMetadataPropertiesGap(
                pageId, "Properties",
                "BC's own page properties are null even though the page's metadata loaded — "
                + "every page shape the compiler produces carries one, so this is a BC-shape gap, "
                + "not a page declaring none of the eleven");

        return new PagePropertiesInfo(
            RefreshOnActivate: properties.RefreshOnActivate,
            ALNamespace: definition.ALNamespace ?? string.Empty,
            InherentPermissions: FormatInherentMask(properties.InherentPermissions, pageId, "InherentPermissions"),
            InherentEntitlements: FormatInherentMask(properties.InherentEntitlements, pageId, "InherentEntitlements"),
            APIVersion: properties.APIVersion ?? string.Empty,
            DataCaptionExpr: properties.DataCaptionExpr ?? string.Empty,
            EntityName: properties.EntityName ?? string.Empty,
            EntitySetName: properties.EntitySetName ?? string.Empty,
            APIPublisher: properties.APIPublisher ?? string.Empty,
            APIGroup: properties.APIGroup ?? string.Empty,
            ChangeTrackingAllowed: properties.ChangeTrackingAllowed);
    }
}
