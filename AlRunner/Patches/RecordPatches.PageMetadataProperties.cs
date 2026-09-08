// RecordPatches.PageMetadataProperties — the eleven "Page Metadata" (2000000138) columns
// that come off a page's <Properties> element (ten of them) or the page definition itself
// (ALNamespace), read from BC's OWN parsed metadata (#3601). Full derivation — the census
// that motivated this, why one object serves both page origins, and the AL-compiler probes
// behind the two non-obvious claims below — is in docs/page-metadata-properties.md.
//
// CLAIM: all eleven are stated on the SAME MetaPageDefinition/MetaPageProperties object
// EnsureRealPageMetadata already loads for the nine <SourceObject> columns one element down
// (#3063), and reading it here is faithful to BC's own PageDataProvider — verified against
// BC 28.1's decompiled PageDataProvider.<GetValuesWithinRangeForKeyField>d__3.MoveNext(),
// which reads every one of them off this same object. See docs/page-metadata-properties.md
// §"one object not two".
//
// CLAIM: InherentPermissions/InherentEntitlements are rendered through BC's OWN
// PermissionDefinition.ExpandDirectPermissionToIndirect and
// MetadataDataProvider.CreatePermissionMaskString (reflection, same pattern as
// PageDataProvider.GenerateSourceTableViewString in the sibling <SourceObject> file), not
// reimplemented (precompiled-dll-respect.md). TRAP: a page can only ever declare `X`
// (Execute) for either — the AL compiler rejects anything else with AL0195 — so do not
// "simplify" this to a literal-string special case; the call graph is what makes it correct
// if BC ever allows more. See docs/page-metadata-properties.md §"InherentPermissions".
//
// CLAIM: DataCaptionExpr. is BC's fixed placeholder "DataCaptionExprCode", never the AL
// source text — confirmed with two different declared expressions producing the identical
// column value, and adjudicated on a real service tier by corpus PR #298 (merged, 8/8 cloud
// legs green). See docs/page-metadata-properties.md §"data-caption-expr".
//
// TRAP: APIVersion's "declares none" default is the literal "beta", not "", per
// MetaPageProperties.APIVersion's own [DefaultValue("beta")] — needs no code here since it
// falls out of reading the property directly, but do not "correct" a future refactor's
// default to "" for this one column. See docs/page-metadata-properties.md §"api-version-default".
//
// REFUSAL POLICY (loud-failures.md): same as the sibling <SourceObject> file — a page whose
// real metadata will not load gets a named refusal, never BC's default. Unlike
// <SourceObject>, MetaPageDefinition.Properties is never null for any page shape the
// compiler produces, so a null one here is refused as a BC-shape gap, not treated as a real
// answer.
//
// AppID is deliberately NOT here — BC reads it from MetadataDataProvider.GetAppId, an
// app-identity surface unrelated to <Properties>. See
// docs/page-metadata-properties.md §"app-id".
//
// PRECOMPILED-DLL RESPECT: runtime-engine and metadata types only (NCLMetaForm,
// MetaPageDefinition, MetaPageProperties, PermissionDefinition, MetadataDataProvider — all
// reached the same way the sibling file reaches PageDataProvider). No AL business-logic
// body is touched.

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
    /// Internal rather than private so <c>PageMetadataPropertiesFormattingTests</c> can pin
    /// this reflection call reaching BC's own runtime-engine formatter without needing a
    /// compiled page: the BC-behaviour claim (what a page's declared mask MEANS) is
    /// adjudicated upstream by corpus PR #298; this is the runner-side wiring underneath it.
    /// </summary>
    internal static string FormatInherentMask(int declaredMask, int pageId, string column)
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
