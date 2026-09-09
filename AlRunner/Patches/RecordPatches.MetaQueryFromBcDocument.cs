// RecordPatches.MetaQueryFromBcDocument — build a compiled query's MetaQuery design object by
// letting BC parse its own emitted metadata document, instead of assembling one property by
// property from SymbolReference.json (issue #3608, part of the chain #3562 tracks).
//
// THIS IS NOT THE USUAL CONVERSION IN THAT CHAIN. The source being replaced is not
// hand-derivation from AL source text: BuildMetaQueryDesign reads a SymbolReference sidecar
// that BC's own SymbolJsonWriter produced from the SAME compilation, so most of what it
// carries is already BC's answer. What it does NOT carry is every property BC's compiler
// ASSIGNS rather than copies, and the runner supplied those from constants:
//
//     hardcoded here                 what BC's document states
//     ---------------------------    -----------------------------------------------------
//     ReadState  = ReadUncommitted   ReadState, per the AL property (ReadShared, …)
//     QueryType  default Normal      QueryType, plus APIPublisher/Group/Version, EntityName,
//                                    EntitySetName, QueryCategory, TopNumberOfRows
//     Distinct   = false             Distinct, per dataitem
//     join type  default InnerJoin   DataItemLinkType, per dataitem
//
// The join-type default is the one with AL-observable consequences, and it was WRONG: AL
// defaults an undeclared SqlJoinType to LeftOuterJoin, not InnerJoin. A query whose nested
// dataitem omits the property therefore dropped every parent row with no matching child.
// Adjudicated on a real service tier by corpus PR #297 (query 60601, codeunit 60602).
//
// Three further columns are compiler-assigned and nothing but the document can supply them:
// ColumnType (the resolved AL type of the source field), MethodType/TotalingMethod, and
// QueryColumnIndex — which the derivation counted by hand across result columns, and which
// BC states as -1 for a filter-only column rather than the design object's default 0.
//
// THE SEAM
//   Types.Metadata.MetaQuery has a PUBLIC constructor MetaQuery(XmlNode, int metadataAppGroupId,
//   int languageAppGroupId) that parses exactly the document AlObjectMetadataRegistry captured
//   (#3548). The result is the same type BuildMetaQueryDesign was assembling, so it feeds the
//   existing NCLMetaQuery.CreateDynamicQuery call unchanged — this replaces how the design
//   object is BUILT, not what is done with it.
//
//   The registry is read directly rather than through RunnerXmlMetadataLoader. That loader is
//   the seam BC's own NCLMetaQuery.LoadMetadata() would use, but this runner does not enter
//   that path for queries: BuildRealNCLMetaQuery calls CreateDynamicQuery, which takes the
//   design object as an argument. Going through the loader would mean wrapping the XML in an
//   NCLObjectXmlMetadata only to unwrap it again.
//
// AVAILABILITY DECIDES THE ROUTE, AND A FAILURE IS NEVER RE-ROUTED
//   The route is taken only when a document is registered for (Query, id). A query living in a
//   PRECOMPILED dependency .app was never emitted here, so no document exists and the
//   SymbolReference derivation stays — that is the only fallback, and it is chosen on
//   availability BEFORE any parse is attempted. A document that fails to parse throws rather
//   than silently falling back to the weaker answer (.claude/rules/loud-failures.md): a
//   fallback on error would put the InnerJoin default back under a green build.
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>The AL compiler's own <c>SymbolKind</c> for a query, which is the key
    /// <see cref="AlObjectMetadataRegistry"/> stores its document under.</summary>
    internal const string BcQueryMetadataKind = "Query";

    private static ConstructorInfo? _ctorMetaQueryFromXml;
    private static readonly object _bcQueryDocumentShapeLock = new();

    /// <summary>
    /// True when BC's emitter handed the runner a metadata document for this query id.
    /// A query with no document — one in a precompiled dependency .app, or one served from a
    /// cache written before #3548 — answers false and keeps the SymbolReference derivation.
    /// </summary>
    internal static bool HasBcQueryMetadataDocument(int queryId)
        => AlObjectMetadataRegistry.TryGet(BcQueryMetadataKind, queryId, out var xml)
           && !string.IsNullOrEmpty(xml);

    /// <summary>
    /// Parse BC's own document for this query into the <c>Types.Metadata.MetaQuery</c> design
    /// object <c>NCLMetaQuery.CreateDynamicQuery</c> consumes. Never called unless
    /// <see cref="HasBcQueryMetadataDocument"/> is true.
    /// </summary>
    private static object BuildMetaQueryDesignFromBcDocument(int queryId)
    {
        AlObjectMetadataRegistry.TryGet(BcQueryMetadataKind, queryId, out var xml);
        EnsureBcQueryDocumentShape();

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        var root = doc.DocumentElement
            ?? throw new BcShapeGapException(
                "AL query metadata construction",
                $"BC's metadata document for query {queryId}",
                "the captured document has no root element");

        // Both group ids are 0: every runner-built object belongs to NavAppGroup.BaseGroup,
        // whose GroupId is 0 (ApplicationObjectBasePatches pokes the same value into every
        // ApplicationObjectBase it fixes up). The second is the LANGUAGE app group, which
        // selects which app's translations captions resolve against — also the base group,
        // since the runner publishes no separate language app.
        return _ctorMetaQueryFromXml!.Invoke(new object?[] { root, 0, 0 })
            ?? throw new BcShapeGapException(
                "AL query metadata construction",
                "Types.Metadata.MetaQuery(XmlNode, int, int)",
                $"BC's parser returned null for query {queryId}");
    }

    /// <summary>
    /// One line per built query naming which of the two routes produced it, and the four values
    /// the routes disagree about. Off unless <c>AL_RUNNER_TRACE_QUERY_METADATA_SOURCE=1</c>.
    ///
    /// <para>This trace is the only place the choice is observable from outside: the two routes
    /// produce the same TYPE, so nothing downstream can be asked which one ran, and three of the
    /// four values (ReadState, QueryType, Distinct) reach no AL surface at all. Same shape and
    /// rationale as <c>AL_RUNNER_TRACE_TABLE_METADATA_SOURCE</c> —
    /// docs/object-metadata-from-bc.md.</para>
    /// </summary>
    private static void TraceQueryMetadataSource(int queryId, string source, object? design)
    {
        if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_QUERY_METADATA_SOURCE") != "1") return;
        Console.Out.WriteLine($"[query-metadata] {queryId} source={source}");
        if (design == null) return;

        string Read(object o, string name) =>
            o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(o)?.ToString() ?? "<none>";

        Console.Out.WriteLine(
            $"[query-metadata] {queryId} readState={Read(design, "ReadState")}"
            + $" queryType={Read(design, "QueryType")}"
            + $" top={Read(design, "TopNumberOfRowsToReturn")}"
            + $" queryCategory='{Read(design, "QueryCategory")}'");

        if (design.GetType().GetProperty("DataItems")?.GetValue(design) is not System.Collections.IEnumerable dis)
            return;
        foreach (var di in dis)
        {
            if (di == null) continue;
            Console.Out.WriteLine(
                $"[query-metadata] {queryId} dataItem={Read(di, "DataItemName")}"
                + $" linkType={Read(di, "DataItemLinkType")}"
                + $" distinct={Read(di, "Distinct")}");
            if (di.GetType().GetProperty("QueryColumns")?.GetValue(di) is not System.Collections.IEnumerable cols)
                continue;
            foreach (var c in cols)
            {
                if (c == null) continue;
                Console.Out.WriteLine(
                    $"[query-metadata] {queryId}   column={Read(c, "Name")}"
                    + $" columnType={Read(c, "ColumnType")}"
                    + $" totalingMethod={Read(c, "FieldTotalingMethod")}"
                    + $" index={Read(c, "QueryColumnIndex")}"
                    + $" filterOnly={Read(c, "FilterOnly")}");
            }
        }
    }

    private static void EnsureBcQueryDocumentShape()
    {
        if (_ctorMetaQueryFromXml != null) return;
        lock (_bcQueryDocumentShapeLock)
        {
            if (_ctorMetaQueryFromXml != null) return;
            EnsureQueryBuilderReflection();
            var t = _tMetaQuery
                ?? throw new BcShapeGapException(
                    "AL query metadata construction",
                    "Microsoft.Dynamics.Nav.Types.Metadata.MetaQuery",
                    "the design type BC's own query metadata parses into");
            _ctorMetaQueryFromXml = t.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, binder: null,
                    types: new[] { typeof(System.Xml.XmlNode), typeof(int), typeof(int) }, modifiers: null)
                ?? throw new BcShapeGapException(
                    "AL query metadata construction",
                    "MetaQuery(XmlNode, int metadataAppGroupId, int languageAppGroupId)",
                    "BC's own parser for the query metadata document its emitter produces — "
                    + "see docs/object-metadata-from-bc.md#queries");
        }
    }
}
